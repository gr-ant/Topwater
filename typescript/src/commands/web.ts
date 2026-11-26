// curl and wget over the shell's web session (Shell.allowWeb); without one every host fails to resolve.
import type { Shell } from '../shell.js';
import { ShellError, reason, specMessage, specOf, type CommandContext } from '../command.js';
import { concatBytes, decodeBytes, encodeText, toBase64 } from '../bytes.js';
import { strftime } from '../format.js';
import { WebFailure, WebSession, WebUrl, decompress, header, type WebResponse } from '../web.js';
import { sleepFor } from './builtins.js';

const NO_NETWORK = new WebSession({ explainBlocked: false });
const utf8 = new TextEncoder();

export function registerWeb(shell: Shell) {
  shell.register('curl', curl);
  shell.register('wget', wget);
}

const sessionOf = (ctx: CommandContext) => ctx.shell.web ?? NO_NETWORK;
/** seconds as ms; 0 or more than a day is a day */
const seconds = (s: number) => s <= 0 || s > 86400 ? 86_400_000 : s * 1000;
const FLOAT = /^\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*$/;
const int32 = (s: string, sign: boolean) => (sign ? /^[+-]?\d+$/ : /^\d+$/).test(s) && Math.abs(Number(s)) <= 2 ** 31 - 1 ? Number(s) : null;

function setHeader(headers: [string, string][], name: string, value: string | null) {
  const i = headers.findIndex(h => h[0].toLowerCase() === name.toLowerCase());
  if (value === null) { if (i >= 0) headers.splice(i, 1); }
  else if (i >= 0) headers[i] = [headers[i][0], value];
  else headers.push([name, value]);
}

/** The last path segment of the URL, as -O and wget name the file (empty for a directory). */
function remoteName(url: WebUrl) {
  const path = url.pathAndQuery.split('?')[0];
  return path.slice(path.lastIndexOf('/') + 1);
}

/** Like .NET's Uri.UnescapeDataString: %XX sequences decoded, anything invalid kept as it is. */
function unescape(s: string) {
  try { return decodeURIComponent(s); } catch { return s.replace(/(%[0-9A-Fa-f]{2})+/g, m => { try { return decodeURIComponent(m); } catch { return m; } }); }
}

// ---------- curl ----------

const CURL_LONG: Record<string, string> = {
  data: 'd', 'data-ascii': 'd', 'data-binary': '\u0001', 'data-raw': '\u0002', 'data-urlencode': '\u0003', json: '\u0004',
  get: 'G', request: 'X', header: 'H', 'user-agent': 'A', user: 'u', location: 'L', 'max-redirs': '\u0005',
  include: 'i', head: 'I', output: 'o', 'remote-name': 'O', fail: 'f', 'fail-with-body': '\u0006', silent: 's',
  'show-error': 'S', 'write-out': 'w', 'max-time': 'm', 'connect-timeout': '\u0007', compressed: '\u0008', verbose: 'v',
  insecure: 'k', help: 'h', version: 'V', retry: '\u0010', 'retry-delay': '\u0011', 'retry-max-time': '\u0012',
  'retry-all-errors': '\u0013', 'retry-connrefused': '\u0014', proto: '\u0015', 'proto-redir': '\u0016', 'fail-early': '\u0017',
  'no-progress-meter': '\u0018', tlsv1: '\u0019', 'tlsv1.0': '\u0019', 'tlsv1.1': '\u0019', 'tlsv1.2': '\u0019', 'tlsv1.3': '\u0019',
  'ssl-reqd': '\u0019', 'http1.1': '\u0019', http2: '\u0019', 'tls-max': '\u001a', 'progress-bar': '\u001b',
};
const CURL_FLAGS = 'GLiIOfsSvkhV\u0006\u0008\u0013\u0014\u0017\u0018\u0019\u001b';
const CURL_VALUED = 'dXHAuowm\u0001\u0002\u0003\u0004\u0005\u0007\u0010\u0011\u0012\u0015\u0016\u001a';

// what --proto names: the protocols curl 8.5 on Ubuntu knows (Topwater itself only speaks http and https)
const CURL_PROTOCOLS = ['dict', 'file', 'ftp', 'ftps', 'gopher', 'gophers', 'http', 'https', 'imap', 'imaps', 'ldap', 'ldaps', 'mqtt',
  'pop3', 'pop3s', 'rtmp', 'rtmpe', 'rtmps', 'rtmpt', 'rtmpte', 'rtmpts', 'rtsp', 'scp', 'sftp', 'smb', 'smbs', 'smtp', 'smtps', 'telnet', 'tftp', 'ws', 'wss'];
const REDIR_PROTOCOLS = ['http', 'https', 'ftp', 'ftps'];

/** --proto and --proto-redir: from `start`, each comma-separated name added (+ or none), removed (-) or made the only one (=);
 * "all" is every protocol. An unknown name gets a warning and is skipped; null when a sign other than these, or none of a
 * name, makes it a usage error. */
function protoSet(spec: string, start: string[], warn: (w: string) => void): Set<string> | null {
  const set = new Set(start);
  for (const token of spec.split(',').filter(t => t !== '')) {
    let i = 0, action = '+';
    for (; i < token.length && !/[A-Za-z0-9]/.test(token[i]); i++) {
      if (!'+-='.includes(token[i])) return null;
      action = token[i];
    }
    if (i === token.length) return null;
    const name = token.slice(i).toLowerCase();
    if (action === '=') set.clear();
    const names = name === 'all' ? CURL_PROTOCOLS : CURL_PROTOCOLS.includes(name) ? [name] : [];
    if (names.length === 0) { warn(specMessage('curl', 'unknownProtocol', { name: token.slice(i) })); continue; }
    for (const n of names) if (action === '-') set.delete(n); else set.add(n);
  }
  return set;
}

/** curl's "Warning: " lines, wrapped at 79 columns (the width curl assumes when stderr is not a terminal). */
function curlWarning(text: string): string {
  const width = 79 - 9;
  let out = '';
  for (let rest = text; rest.length > 0;) {
    if (rest.length <= width) { out += specMessage('curl', 'warning', { text: rest }) + '\n'; break; }
    let cut = width - 1;
    while (cut > 0 && rest[cut] !== ' ' && rest[cut] !== '\t') cut--;
    if (cut === 0) cut = width - 1;
    out += specMessage('curl', 'warning', { text: rest.slice(0, cut + 1) }) + '\n';
    rest = rest.slice(cut + 1);
  }
  return out;
}

const isLetter = (c: string) => /^[A-Za-z]$/.test(c);

async function curl(ctx: CommandContext): Promise<number> {
  const web = sessionOf(ctx);
  const spec = specOf('curl');
  const M = (key: string, values: Record<string, string> = {}) => specMessage('curl', key, values);
  const usage = (message: string) => { ctx.stderr.write(message + '\n' + spec.tryHelp); return 2; };
  const opts: { key: string; value: string | null; name: string }[] = [];
  const urls: string[] = [];
  const args = ctx.args;
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg.startsWith('--') && arg.length > 2) {
      const key = Object.hasOwn(CURL_LONG, arg.slice(2)) ? CURL_LONG[arg.slice(2)] : undefined;
      if (key === undefined) return usage(M('unknownOption', { option: arg }));
      if (CURL_VALUED.includes(key)) {
        if (++i >= args.length) return usage(M('needsParameter', { option: arg }));
        opts.push({ key, value: args[i], name: arg });
      } else opts.push({ key, value: null, name: arg });
    } else if (arg.startsWith('-') && arg.length > 1) {
      for (let j = 1; j < arg.length; j++) {
        const c = arg[j];
        if (c === '#') { opts.push({ key: '\u001b', value: null, name: '-#' }); continue; }
        if (CURL_VALUED.includes(c) && isLetter(c)) {
          const value = j + 1 < arg.length ? arg.slice(j + 1) : ++i < args.length ? args[i] : null;
          if (value === null) return usage(M('needsParameter', { option: '-' + c }));
          opts.push({ key: c, value, name: '-' + c });
          break;
        }
        if (!CURL_FLAGS.includes(c) || !isLetter(c)) return usage(M('unknownOption', { option: '-' + c }));
        opts.push({ key: c, value: null, name: '-' + c });
      }
    } else urls.push(arg);
  }

  const has = (k: string) => opts.some(o => o.key === k);
  const last = (k: string) => opts.findLast(o => o.key === k)?.value ?? null;
  if (has('h')) { ctx.stdout.write(spec.help ?? ''); return 0; }
  if (has('V')) { ctx.stdout.write(spec.version + '\n'); return 0; }
  if (urls.length === 0) { ctx.stderr.write(spec.tryHelp); return 2; }

  const silent = has('s') && !has('S'), head = has('I'), include = has('i') || head, verbose = has('v');
  const fail = has('f'), failWithBody = has('\u0006'), follow = has('L'), compressed = has('\u0008'), get = has('G');
  const error = (code: number, message: string) => { if (!silent) ctx.stderr.write(M('error', { code: String(code), message }) + '\n'); };

  let maxTime = 0, maxRedirs = -1;
  for (const { key, value, name } of opts.filter(o => o.key === 'm' || o.key === '\u0007' || o.key === '\u0005')) {
    if (key === '\u0005') {
      const n = int32(value!, true);
      if (n === null) return usage(M('badNumber', { option: name }));
      maxRedirs = n;
    } else if (!FLOAT.test(value!) || Number(value) < 0) return usage(M('badNumber', { option: name }));
    else maxTime = maxTime === 0 ? Number(value) : Math.min(maxTime, Number(value));
  }
  const timeoutMs = maxTime > 0 && seconds(maxTime) < web.policy.timeoutMs ? seconds(maxTime) : web.policy.timeoutMs;

  // --retry, --retry-delay, --retry-max-time: whole seconds, not negative
  const retry = [0, 0, 0];
  for (const { key, value, name } of opts.filter(o => o.key === '\u0010' || o.key === '\u0011' || o.key === '\u0012')) {
    if (!/^[+-]?\d+$/.test(value!)) return usage(M('badNumber', { option: name }));
    const n = Number(value);
    if (n < 0) return usage(M('negativeNumber', { option: name }));
    retry[key.charCodeAt(0) - 0x10] = n;
  }
  const [retries, retryDelay, retryMaxTime] = retry;
  const retryAll = has('\u0013'), retryRefused = has('\u0014'), failEarly = has('\u0017');
  let protocols = new Set(CURL_PROTOCOLS), redirProtocols = new Set(REDIR_PROTOCOLS);
  for (const { key, value, name } of opts.filter(o => o.key === '\u0015' || o.key === '\u0016')) {
    const set = protoSet(value!, key === '\u0015' ? CURL_PROTOCOLS : REDIR_PROTOCOLS, w => ctx.stderr.write(curlWarning(w)));
    if (set === null) return usage(M('badlyUsed', { option: name }));
    if (key === '\u0015') protocols = set; else redirProtocols = set;
  }

  // the body: -d values joined with &, as curl does
  const parts: Uint8Array[] = [];
  let json = false, anyData = false;
  for (const { key, value, name } of opts.filter(o => 'd\u0001\u0002\u0003\u0004'.includes(o.key))) {
    anyData = true;
    json ||= key === '\u0004';
    let bytes: Uint8Array;
    try {
      if (key === '\u0003') bytes = utf8.encode(urlEncodeData(value!));
      else if (key !== '\u0002' && value!.startsWith('@')) {
        const file = value!.slice(1);
        bytes = file === '-' ? encodeText(ctx.stdinText) : ctx.fs.readBytes(ctx.resolvePath(file));
        if (key === 'd' || key === '\u0004') bytes = bytes.filter(b => b !== 13 && b !== 10);
      } else bytes = encodeText(value!);
    } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      ctx.stderr.write(M('readFile', { option: name }) + '\n' + spec.tryHelp);
      return 26;
    }
    parts.push(bytes);
  }
  let body: Uint8Array | null = anyData ? join(parts, json ? new Uint8Array(0) : utf8.encode('&')) : null;
  const query = get && body !== null ? new TextDecoder().decode(body) : null;
  if (get) body = null;

  const headers: [string, string][] = [['User-Agent', 'curl/8.5.0'], ['Accept', '*/*']];
  if (body !== null) setHeader(headers, 'Content-Type', json ? 'application/json' : 'application/x-www-form-urlencoded');
  if (json) setHeader(headers, 'Accept', 'application/json');
  if (compressed) setHeader(headers, 'Accept-Encoding', 'deflate, gzip');
  const agent = last('A');
  if (agent !== null) setHeader(headers, 'User-Agent', agent.length === 0 ? null : agent);
  for (const h of opts.filter(o => o.key === 'H').map(o => o.value!)) {
    const colon = h.indexOf(':'), semi = h.indexOf(';');
    if (colon > 0 && (semi < 0 || colon < semi)) {
      const v = h.slice(colon + 1).trim();
      setHeader(headers, h.slice(0, colon).trim(), v.length === 0 ? null : v);
    } else if (semi > 0 && h.slice(semi + 1).trim().length === 0) setHeader(headers, h.slice(0, semi).trim(), '');
  }
  const user = last('u');

  const method = head ? 'HEAD' : body !== null ? 'POST' : 'GET';
  const forced = last('X');
  const outputs = opts.filter(o => o.key === 'o' || o.key === 'O');
  const format = last('w');
  let exit = 0;

  for (const raw of urls) {
    const output = outputs.shift() ?? { key: '', value: null };
    const started = ctx.shell.now();
    let code = 0, redirects = 0;
    let lastResponse: WebResponse | null = null;
    let vars: Record<string, string> = {};
    for (let tries = 0; ; tries++) {
      let buffer: Uint8Array[] = [];
      let message = '', urlText = raw, retriable = true;
      let target: string | null = null;
      code = 0; redirects = 0; lastResponse = null;
      const hops: { response: WebResponse; at: number; requestBytes: number }[] = [];
      // on a virtual clock the waits take no time: the call's limit ends the tries; give the host's event loop a turn now and then
      ctx.checkTime();
      if (tries % 256 === 255) await new Promise(r => setTimeout(r, 0));
      const clock = ctx.shell.now(); // the shell's clock, so a recording on a virtual one replays
      const elapsed = () => (ctx.shell.now() - clock) / 1000;
      try {
        if (query !== null) urlText += (raw.includes('?') ? '&' : '?') + query;
        const url = WebUrl.parse(urlText);
        if (!protocols.has(url.scheme)) throw new WebFailure('protocol', url.scheme);
        const sent: [string, string][] = [...headers];
        const userInfo = user ?? (url.userInfo !== null ? unescape(url.userInfo) : null);
        if (userInfo !== null && !sent.some(h => h[0].toLowerCase() === 'authorization'))
          sent.push(['Authorization', 'Basic ' + toBase64(utf8.encode(userInfo.includes(':') ? userInfo : userInfo + ':'))]);
        target = output.key === 'O' ? remoteName(url) : output.value;
        if (target === '') throw new WebFailure('recv', '', 0, null, 'noRemoteName');

        const onHop = (r: WebResponse) => {
          lastResponse = r;
          // shortcut: the body counts toward the first request only; a 303 drops it, other redirects resend it
          const first = body !== null && hops.length === 0;
          const request = `${r.method} ${r.url.pathAndQuery} HTTP/1.1\r\nHost: ${r.url.hostForUrl}${r.url.defaultPort ? '' : ':' + r.url.port}\r\n`
            + sent.map(([k, v]) => `${k}: ${v}\r\n`).join('') + (first ? `Content-Length: ${body!.length}\r\n` : '') + '\r\n';
          hops.push({ response: r, at: elapsed(), requestBytes: utf8.encode(request).length + (first ? body!.length : 0) });
          if (verbose) {
            let s = `> ${r.method} ${r.url.pathAndQuery} HTTP/1.1\r\n> Host: ${r.url.hostForUrl}${r.url.defaultPort ? '' : ':' + r.url.port}\r\n`;
            for (const [k, v] of sent) s += `> ${k}: ${v}\r\n`;
            s += `> \r\n< HTTP/1.1 ${r.status} ${r.reason}\r\n`;
            for (const [k, v] of r.headers) s += `< ${k}: ${v}\r\n`;
            ctx.stderr.write(s + '< \r\n');
          }
          if (include) buffer.push(utf8.encode(statusBlock(r)));
          // --proto-redir (and --proto) decide where a redirect may lead
          const location = header(r, 'Location');
          if (follow && [301, 302, 303, 307, 308].includes(r.status) && location) {
            const next = r.url.resolve(location).scheme;
            if (!(protocols.has(next) && redirProtocols.has(next))) throw new WebFailure('protocol', next);
          }
        };
        const result = await web.fetch(url, method, forced, sent, body, follow, maxRedirs, timeoutMs, onHop, ctx);
        const r = lastResponse = result.response;
        redirects = result.redirects;
        let data = r.body;
        const encoding = header(r, 'Content-Encoding')?.trim().toLowerCase();
        if (compressed && !r.truncated && (encoding === 'gzip' || encoding === 'x-gzip' || encoding === 'deflate') && data.length > 0)
          data = decode(data, encoding !== 'deflate', web.policy.maxResponseBytes, !r.error);
        if ((fail || failWithBody) && r.status >= 400) {
          if (failWithBody) buffer.push(data);
          code = 22;
          message = M('httpError', { status: String(r.status) });
        } else {
          if (!head) buffer.push(data);
          if (r.truncated) { code = 63; message = M('tooLarge'); }
          else if (r.error) [code, message] = cutShort(r);
        }
        if (target !== null && target !== '-' && ([0, 18, 56, 63].includes(code) || failWithBody)) {
          try { ctx.fs.writeBytes(ctx.resolvePath(target), concatBytes(buffer)); } catch (e) {
            if (!(e instanceof ShellError)) throw e;
            code = 23; message = M('writeFailed');
          }
          buffer = [];
        }
      } catch (e) {
        if (!(e instanceof WebFailure)) throw e;
        [code, message] = curlError(e, web.policy.explainBlocked);
        retriable = e.blocked === null && e.kind !== 'replay'; // the web policy's answer would not change
      }
      const out = concatBytes(buffer);
      if (out.length > 0) ctx.stdout.write(decodeBytes(out));
      if (code !== 0) error(code, message);

      const total = elapsed();
      const lr = lastResponse as WebResponse | null;
      const size = lr?.body.length ?? 0, upload = body?.length ?? 0;
      const secs = (x: number) => x.toFixed(6);
      vars = {
        errormsg: code === 0 ? '' : message, filename_effective: target === null || target === '-' ? '' : target, url: urlText,
        scheme: lr?.url.scheme.toUpperCase() ?? '', http_version: lr === null ? '0' : '1.1', remote_port: lr ? String(lr.url.port) : '',
        num_connects: String(hops.filter((h, k) => k === 0 || h.response.url.host !== hops[k - 1].response.url.host || h.response.url.port !== hops[k - 1].response.url.port).length),
        size_upload: String(upload), size_header: String(hops.reduce((n, h) => n + utf8.encode(statusBlock(h.response)).length, 0)),
        size_request: String(hops.reduce((n, h) => n + h.requestBytes, 0)),
        speed_download: String(total > 0 ? Math.floor(size / total) : 0), speed_upload: String(total > 0 && hops.length > 0 ? Math.floor(upload / total) : 0),
        // the phases before the first response byte are not timed apart
        time_total: secs(total), time_namelookup: secs(0), time_connect: secs(0), time_appconnect: secs(0), time_pretransfer: secs(0),
        time_starttransfer: secs(hops.length > 0 ? hops[hops.length - 1].at : 0), time_redirect: secs(hops.length > 1 ? hops[hops.length - 2].at : 0),
      };

      // --retry: a timeout or unresolved name, a refused connection (--retry-connrefused), a transient HTTP status, or anything (--retry-all-errors)
      const status = lr?.status ?? 0;
      const why = !retriable || tries >= retries ? null
        : code === 6 || code === 28 ? 'retryTimeout'
        : code === 7 && retryRefused ? 'retryRefused'
        : (code === 0 || code === 22) && [408, 429, 500, 502, 503, 504].includes(status) ? 'retryHttp'
        : retryAll && code !== 0 ? 'retryAll' : null;
      if (why === null || retryMaxTime > 0 && (ctx.shell.now() - started) / 1000 >= retryMaxTime) break;
      let wait = retryDelay > 0 ? retryDelay : Math.min(600, 2 ** Math.min(tries, 10));
      // an HTTP retry waits at least the Retry-After seconds (shortcut: the HTTP-date form is not read)
      const after = why === 'retryHttp' && lr ? header(lr, 'Retry-After')?.trim() : undefined;
      if (after !== undefined && /^\d+$/.test(after) && Number(after) > wait) wait = Number(after);
      if (!has('s')) // -S brings back errors, not warnings
        ctx.stderr.write(curlWarning(M('retrying', { why: M(why), seconds: String(wait), left: String(retries - tries) })));
      await sleepFor(ctx, Math.min(wait, 86400) * 1000); // a longer wait never ends before the call does
    }
    exit = code; // without --fail-early, the last URL's status
    if (format !== null) ctx.stdout.write(writeOut(ctx, format, lastResponse, redirects, code, method, forced, vars));
    if (failEarly && code !== 0) break;
  }
  return exit;
}

/** A response whose body ended early (written as far as it came): closed, or reset. */
function cutShort(r: WebResponse): [number, string] {
  const M = (key: string, values: Record<string, string> = {}) => specMessage('curl', key, values);
  if (r.error === 'reset') return [56, M('reset')];
  const length = header(r, 'Content-Length')?.trim() ?? '';
  return /^[0-9]+$/.test(length) && Number(length) > r.body.length ? [18, M('partial', { bytes: String(Number(length) - r.body.length) })] : [18, M('partialChunked')];
}

function curlError(f: WebFailure, explain: boolean): [number, string] {
  const M = (key: string, values: Record<string, string> = {}) => specMessage('curl', key, values);
  if (f.blocked !== null && (explain || f.kind === 'policy')) return [f.kind === 'resolve' ? 6 : f.kind === 'connect' ? 7 : 1, M('blocked', { reason: f.blocked })];
  switch (f.kind) {
    case 'badUrl': return [3, M('badUrl')];
    case 'badHostname': return [3, M('badHostname')];
    case 'protocol': return [1, M('protocol', { scheme: f.host })];
    case 'resolve': return [6, M('resolve', { host: f.host })];
    case 'connect': return [7, M('connect', { host: f.host, port: String(f.port) })];
    case 'timeout': return [28, M('timeout', { ms: f.detail, bytes: '0' })];
    case 'tls': return [60, M('tls', { host: f.host })];
    case 'redirects': return [47, M('redirects', { max: f.detail })];
    case 'badHeader': return [43, M('badHeader')];
    case 'tooLarge': return [63, M('tooLarge')];
    case 'replay': return [7, M('replay', { request: f.detail })];
    case 'partial': return [18, M('partialChunked')];
    case 'reset': return [56, M('reset')];
    case 'weird': return [8, M('weird')];
    case 'empty': return [52, M('empty')];
    default: return f.kind === 'recv' && f.detail === 'noRemoteName' ? [23, M('noRemoteName')] : [56, M('recv')];
  }
}

function statusBlock(r: WebResponse) {
  let s = `HTTP/1.1 ${r.status} ${r.reason}\r\n`;
  for (const [k, v] of r.headers) s += `${k}: ${v}\r\n`;
  return s + '\r\n';
}

function join(parts: Uint8Array[], separator: Uint8Array) {
  return concatBytes(parts.flatMap((p, i) => i > 0 ? [separator, p] : [p]));
}

/** --data-urlencode: "name=value" encodes the value, "=value" and "value" encode it all; space is +. */
function urlEncodeData(s: string) {
  const enc = (v: string) => Array.from(utf8.encode(v), b => {
    const c = String.fromCharCode(b);
    return /[A-Za-z0-9._~-]/.test(c) ? c : b === 32 ? '+' : '%' + b.toString(16).toUpperCase().padStart(2, '0');
  }).join('');
  const eq = s.indexOf('=');
  return eq < 0 ? enc(s) : eq === 0 ? enc(s.slice(1)) : s.slice(0, eq) + '=' + enc(s.slice(eq + 1));
}

/** gzip or zlib-wrapped deflate, at most `max` bytes out. Throws WebFailure: tooLarge, or recv when the data is corrupt. */
function decode(data: Uint8Array, gzip: boolean, max: number, whole = true): Uint8Array {
  let out: Uint8Array;
  try { out = decompress(data, gzip, max + 1, whole); } catch { throw new WebFailure('recv'); }
  if (out.length > max) throw new WebFailure('tooLarge');
  return out;
}

function writeOut(ctx: CommandContext, format: string, r: WebResponse | null, redirects: number, code: number, method: string, forced: string | null, vars: Record<string, string>): string {
  let s = '';
  for (let i = 0; i < format.length; i++) {
    const c = format[i];
    if (c === '\\' && i + 1 < format.length && 'ntr\\'.includes(format[i + 1])) {
      const n = format[++i];
      s += n === 'n' ? '\n' : n === 't' ? '\t' : n === 'r' ? '\r' : '\\';
      continue;
    }
    if (c !== '%' || i + 1 >= format.length) { s += c; continue; }
    if (format[i + 1] === '%') { s += '%'; i++; continue; }
    const close = format.indexOf('}', i);
    if (format[i + 1] !== '{' || close < 0) { s += c; continue; }
    const name = format.slice(i + 2, close);
    i = close;
    const location = r ? header(r, 'Location') : undefined;
    switch (name) {
      case 'http_code': case 'response_code': s += String(r?.status ?? 0).padStart(3, '0'); break;
      case 'content_type': s += (r && header(r, 'Content-Type')) ?? ''; break;
      case 'size_download': s += String(r?.body.length ?? 0); break;
      case 'url_effective': s += r?.url.toString() ?? ''; break;
      case 'redirect_url':
        if (r && r.status >= 300 && r.status < 400 && location) { try { s += r.url.resolve(location).toString(); } catch (e) { if (!(e instanceof WebFailure)) throw e; s += location; } }
        break;
      case 'num_redirects': s += String(redirects); break;
      case 'method': s += forced ?? r?.method ?? method; break;
      case 'exitcode': s += String(code); break;
      default:
        if (Object.hasOwn(vars, name)) s += vars[name];
        else ctx.stderr.write(specMessage('curl', 'unknownWriteOut', { name }) + '\n');
    }
  }
  return s;
}

// ---------- wget ----------

const WGET_LONG: Record<string, string> = {
  'output-document': 'O', quiet: 'q', 'server-response': 'S', header: '\u0001', 'post-data': '\u0002', 'post-file': '\u0003',
  'user-agent': 'U', timeout: 'T', 'max-redirect': '\u0004', 'no-check-certificate': '\u0005', help: 'h', version: 'V',
  'no-verbose': '\u0006', 'directory-prefix': 'P', 'no-clobber': '\u0007', spider: '\u0008', tries: 't', 'show-progress': '\u0009',
};
const WGET_VALUED = 'OUTPt\u0001\u0002\u0003\u0004';

async function wget(ctx: CommandContext): Promise<number> {
  const web = sessionOf(ctx);
  const spec = specOf('wget');
  const M = (key: string, values: Record<string, string> = {}) => specMessage('wget', key, values);
  const usage = (message: string, code = 2) => { ctx.stderr.write(message + '\n' + spec.tryHelp); return code; };
  const opts: { key: string; value: string | null }[] = [];
  const urls: string[] = [];
  const args = ctx.args;
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === '--') { urls.push(...args.slice(i + 1)); break; }
    if (arg.startsWith('--')) {
      const eq = arg.indexOf('=');
      const name = eq < 0 ? arg.slice(2) : arg.slice(2, eq);
      const key = Object.hasOwn(WGET_LONG, name) ? WGET_LONG[name] : undefined;
      if (key === undefined) return usage(M('unrecognizedOption', { option: eq < 0 ? arg : arg.slice(0, eq) }));
      if (WGET_VALUED.includes(key)) {
        const value = eq >= 0 ? arg.slice(eq + 1) : ++i < args.length ? args[i] : null;
        if (value === null) return usage(M('longRequiresArgument', { option: arg }));
        opts.push({ key, value });
      } else opts.push({ key, value: null });
    } else if (arg.startsWith('-') && arg.length > 1) {
      for (let j = 1; j < arg.length; j++) {
        const c = arg[j];
        // -nv and -nc: wget's two-letter short options
        if (c === 'n' && j + 1 < arg.length && (arg[j + 1] === 'v' || arg[j + 1] === 'c')) { opts.push({ key: arg[++j] === 'v' ? '\u0006' : '\u0007', value: null }); continue; }
        if (!'OqSUThVPt'.includes(c)) return usage(M('invalidOption', { option: c }));
        if (WGET_VALUED.includes(c)) {
          const value = j + 1 < arg.length ? arg.slice(j + 1) : ++i < args.length ? args[i] : null;
          if (value === null) return usage(M('requiresArgument', { option: c }));
          opts.push({ key: c, value });
          break;
        }
        opts.push({ key: c, value: null });
      }
    } else urls.push(arg);
  }
  const has = (k: string) => opts.some(o => o.key === k);
  const last = (k: string) => opts.findLast(o => o.key === k)?.value ?? null;
  if (has('h')) { ctx.stdout.write(spec.help ?? ''); return 0; }
  if (has('V')) { ctx.stdout.write(spec.version + '\n'); return 0; }
  if (urls.length === 0) return usage(M('missingUrl'), 1);

  const quiet = has('q'), server = has('S'), brief = has('\u0006'), spider = has('\u0008'), noClobber = has('\u0007');
  const dir = last('P');
  const prefix = dir ? dir.replace(/\/+$/, '') + '/' : null;
  let timeoutMs = web.policy.timeoutMs;
  const t = last('T');
  if (t !== null) {
    if (!FLOAT.test(t) || Number(t) < 0) return usage(M('badNumber', { option: '--timeout', value: t }));
    const s = Number(t);
    if (s > 0 && seconds(s) < timeoutMs) timeoutMs = seconds(s);
  }
  let maxRedirect = 20;
  const mr = last('\u0004');
  if (mr !== null) {
    const n = int32(mr, false);
    if (n === null) return usage(M('badNumber', { option: '--max-redirect', value: mr }));
    maxRedirect = n;
  }

  const headers: [string, string][] = [['User-Agent', 'Wget/1.21.4'], ['Accept', '*/*'], ['Accept-Encoding', 'identity']];
  let body: Uint8Array | null = null;
  const post = last('\u0002');
  if (post !== null) body = encodeText(post);
  const postFile = last('\u0003');
  if (postFile !== null) {
    try { body = ctx.fs.readBytes(ctx.resolvePath(postFile)); } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      ctx.stderr.write(M('readFile', { file: postFile, errno: reason(e) }) + '\n');
      return 3;
    }
  }
  if (body !== null) setHeader(headers, 'Content-Type', 'application/x-www-form-urlencoded');
  const ua = last('U');
  if (ua !== null) setHeader(headers, 'User-Agent', ua);
  for (const h of opts.filter(o => o.key === '\u0001').map(o => o.value!)) {
    const colon = h.indexOf(':');
    if (colon <= 0) return usage(M('invalidHeader', { header: h }));
    setHeader(headers, h.slice(0, colon).trim(), h.slice(colon + 1).trim());
  }

  // -O FILE is created before anything is fetched, and every body is appended to it
  const document = last('O');
  if (document !== null && document !== '-') {
    try { ctx.fs.writeBytes(ctx.resolvePath(document), new Uint8Array(0)); } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      ctx.stderr.write(M('writeFailed', { file: document, errno: reason(e) }) + '\n');
      return 1;
    }
  }

  const now = () => strftime(ctx.shell.now(), '%Y-%m-%d %H:%M:%S');
  const say = (line: string) => { if (!quiet && !brief) ctx.stderr.write(line + '\n'); }; // the full report
  const note = (line: string) => { if (!quiet) ctx.stderr.write(line + '\n'); }; // also under -nv
  let exit = 0;
  for (const raw of urls) {
    let code = 0;
    let url: WebUrl;
    try { url = WebUrl.parse(raw); } catch (e) {
      if (!(e instanceof WebFailure)) throw e;
      ctx.stderr.write((e.kind === 'protocol' ? M('scheme', { url: raw, scheme: e.host }) : M('badUrl', { url: raw })) + '\n');
      exit = 1;
      continue;
    }
    // the file a download is saved to: -O, or the URL's last segment (index.html), under -P
    const fileFor = (u: WebUrl) => { const name = remoteName(u); return (prefix ?? '') + (name.length > 0 ? unescape(name) : 'index.html'); };
    if (noClobber && document === null && !spider && ctx.fs.exists(ctx.resolvePath(fileFor(url)))) {
      say(M('clobber', { file: fileFor(url) }));
      say('');
      continue;
    }
    if (spider) say(M('spiderMode'));
    say(M('start', { time: now(), url: url.toString() }));
    let hop = 0;
    const onHop = (r: WebResponse) => {
      if (hop++ > 0) { if (spider) say(M('spiderMode')); say(M('start', { time: now(), url: r.url.toString() })); }
      say(M('sent', { status: server ? '' : `${r.status} ${r.reason}` }));
      if (server) {
        let s = `  HTTP/1.1 ${r.status} ${r.reason}\n`;
        for (const [k, v] of r.headers) s += `  ${k}: ${v}\n`;
        ctx.stderr.write(s);
      }
      const loc = header(r, 'Location');
      if ([301, 302, 303, 307, 308].includes(r.status) && loc) say(M('following', { location: loc }));
    };
    try {
      const { response: r } = await web.fetch(url, spider ? 'HEAD' : body !== null ? 'POST' : 'GET', null, [...headers], body, true, maxRedirect, timeoutMs, onHop, ctx);
      if (r.status >= 400) {
        if (brief) note(M('nvErrorUrl', { url: r.url.toString() }));
        note(spider ? M('spiderMissing') : M('httpError', { time: now(), status: `${r.status}: ${r.reason}` }));
        say('');
        code = 8;
      } else if (spider) {
        const type = header(r, 'Content-Type');
        say(M('length', { length: (header(r, 'Content-Length') ?? M('lengthUnspecified')) + (type !== undefined ? ` [${type}]` : '') }));
        say(M('spiderExists'));
        say('');
        if (brief) note(M('nvSpider', { time: now(), url: r.url.toString(), status: `${r.status} ${r.reason}` }));
      } else {
        const length = header(r, 'Content-Length'), type = header(r, 'Content-Type');
        say(M('length', { length: (length ?? M('lengthUnspecified')) + (type !== undefined ? ` [${type}]` : '') }));
        let file = document;
        if (file === null) {
          file = fileFor(url);
          if (prefix !== null) {
            try { ctx.fs.mkdir(ctx.resolvePath(prefix), true); } catch (e) {
              if (!(e instanceof ShellError)) throw e;
              ctx.stderr.write(M('writeFailed', { file, errno: reason(e) }) + '\n');
              exit = 3;
              continue;
            }
          }
          if (ctx.fs.exists(ctx.resolvePath(file))) {
            let n = 1;
            while (ctx.fs.exists(ctx.resolvePath(`${file}.${n}`))) n++;
            file = `${file}.${n}`;
          }
        }
        say(M('saving', { file: file === '-' ? 'STDOUT' : file }));
        if (file === '-') ctx.stdout.write(decodeBytes(r.body));
        else {
          try {
            const path = ctx.resolvePath(file);
            ctx.fs.writeBytes(path, document !== null ? concatBytes([ctx.fs.readBytes(path), r.body]) : r.body);
          } catch (e) {
            if (!(e instanceof ShellError)) throw e;
            ctx.stderr.write(M('writeFailed', { file, errno: reason(e) }) + '\n');
            exit = 3;
            continue;
          }
        }
        say('');
        const size = String(r.body.length) + (length !== undefined ? '/' + length : '');
        if (r.error) { // the body ended early: kept as far as it came
          note(M(r.error === 'reset' ? 'readError' : 'closed', { time: now(), size: String(r.body.length) }));
          say('');
          code = 4;
        } else {
          say(file === '-' ? M('written', { time: now(), size }) : M('saved', { time: now(), file, size }));
          say('');
        }
        if (brief && !r.error) note(M('nvSaved', { time: now(), url: r.url.toString(), size, file }));
        if (r.truncated) { ctx.stderr.write(M('tooLarge', { limit: String(web.policy.maxResponseBytes) }) + '\n'); code = 1; }
      }
    } catch (e) {
      if (!(e instanceof WebFailure)) throw e;
      const [c, message] = wgetError(e, web.policy.explainBlocked);
      code = c;
      if (message.length > 0 && (!quiet || e.blocked !== null && web.policy.explainBlocked || e.kind === 'policy' || e.kind === 'replay' || e.kind === 'badHeader')) ctx.stderr.write(message + '\n');
    }
    if (code !== 0) exit = code;
  }
  return exit;
}

function wgetError(f: WebFailure, explain: boolean): [number, string] {
  const M = (key: string, values: Record<string, string> = {}) => specMessage('wget', key, values);
  if (f.blocked !== null && (explain || f.kind === 'policy')) return [f.kind === 'resolve' || f.kind === 'connect' ? 4 : 1, M('blocked', { reason: f.blocked })];
  switch (f.kind) {
    case 'badUrl': case 'badHostname': return [1, M('badUrl', { url: f.host })];
    case 'protocol': return [1, M('scheme', { url: f.host + '://', scheme: f.host })];
    case 'resolve': return [4, M('resolve', { host: f.host })];
    case 'connect': return [4, M('connect', { host: f.host, port: String(f.port) })];
    case 'timeout': return [4, M('timeout')];
    case 'tls': return [5, M('tls', { host: f.host })];
    case 'redirects': return [8, M('redirects', { max: f.detail })];
    case 'badHeader': return [2, M('invalidHeader', { header: '' })];
    case 'replay': return [4, M('replay', { request: f.detail })];
    default: return [4, M('recv')];
  }
}
