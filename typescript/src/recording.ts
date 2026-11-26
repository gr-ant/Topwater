// Recordings as .topwater files (spec/topwater-file): a zip with the machine after the last call (manifest.json),
// history.jsonl (a header, then one line per call) and checkpoints/<n>.json, the machine after n calls. All three ports
// write and read it. The JSON Lines recordings this replaced (snapshots embedded or in side-car files) still load.
import { DEFAULTS, Shell, type ShellOptions, type ShellResult } from './shell.js';
import { HOST, ShellSnapshot, SnapshotError, USER, openTopwater, parseTime, sha256, writeTopwater } from './snapshot.js';
import { WebSession, type WebExchange, type WebPolicy } from './web.js';
import { sameEntry, type VfsEntry } from './vfs.js';

export const RECORDING_FORMAT_VERSION = 1;
const FORMAT = 'topwater-recording', HISTORY = 'topwater-history', HISTORY_VERSION = 1;
/** Most snapshots one recording may hold (the start included). */
export const MAX_SNAPSHOTS = 1000;
/** The files of all a recording's snapshots together may hold at most this many times the limits' maxFileSystemBytes. */
export const MAX_SNAPSHOTS_FILE_SYSTEMS = 4;
const LIMITS = ['maxLoopIterations', 'maxExtractBytes', 'maxExtractEntries', 'maxCallDurationMs', 'maxFileSystemBytes', 'maxStringBytes'] as const;

/** The ShellOptions a recording stores (everything but the filesystem). */
export type RecordedOptions = Partial<Omit<ShellOptions, 'fileSystem'>>;

export interface RecordedCall {
  index: number;
  command: string;
  stdout: string;
  stderr: string;
  exitCode: number;
  trailer?: string;
  /** wall time; informational, never compared */
  elapsedMs: number;
  /** the shell's clock (ms) when the call started: the virtual clock on a deterministic shell */
  time?: number;
  /** absolute paths the call added, changed or removed, sorted */
  files?: string[];
  /** the machine right after this call, when the recorder took a snapshot here */
  snapshot?: ShellSnapshot;
  /** the web requests the call made and what came back, in order */
  web?: WebExchange[];
}

export interface ReplayDifference { index: number; command: string; field: 'stdout' | 'stderr' | 'exitCode' | 'trailer'; expected: string; actual: string }

export class RecordingError extends Error {
  constructor(why: string) { super('not a valid Topwater recording: ' + why); }
}
const bad = (why: string) => new RecordingError(why);

export class Recording {
  created = Date.now();
  topwater?: string = 'typescript';
  metadata: Record<string, string> = {};
  /** the app's own commands the shell had: a replay target needs them registered too */
  appCommands: string[] = [];
  calls: RecordedCall[] = [];
  /** The web access the shell had (allowWeb), credentials as host names only (their values are ""), no transport. A shell
   * made from the recording serves the recorded responses under it and never touches the network. */
  web?: WebPolicy;

  constructor(public start: ShellSnapshot, public options: RecordedOptions) {}

  /** True when the run had a virtual clock and a random seed, so a replay on the same port and version must match exactly. */
  get isDeterministic() { return this.options.startTime !== undefined && this.options.randomSeed !== undefined; }

  /**
   * A fresh shell at the recording's start (or `start`): its options, then `configure` (register your commands), then the
   * snapshot. `firstCall` is the call it runs first, so its web requests get that call's recorded responses.
   */
  createShell(configure?: (shell: Shell) => void, start: ShellSnapshot = this.start, firstCall = 0): Shell {
    const shell = new Shell({ ...this.options, user: start.user, hostname: start.hostname });
    configure?.(shell);
    // the recorded responses, never the network (even when configure allowed it), and no credential is looked up
    shell.web = this.web
      ? new WebSession({ ...this.web, transport: undefined, credentials: Object.fromEntries(Object.keys(this.web.credentials ?? {}).map(h => [h, () => ''])) },
        this.calls.slice(firstCall).flatMap(c => c.web ?? []))
      : new WebSession({ explainBlocked: false }, []);
    start.restore(shell);
    return shell;
  }

  /**
   * The machine as it was right after call `index` (-1: before the first): restored from the nearest snapshot at or before
   * it, then the calls after that run again. `onCall` sees each re-run call's result.
   */
  async shellAt(index: number, configure?: (shell: Shell) => void, onCall?: (call: RecordedCall, actual: ShellResult) => void): Promise<Shell> {
    if (!Number.isInteger(index) || index < -1 || index >= this.calls.length) throw new RangeError(`no call ${index}`);
    let from = index;
    while (from >= 0 && !this.calls[from].snapshot) from--;
    const shell = this.createShell(configure, from >= 0 ? this.calls[from].snapshot : this.start, from + 1);
    for (let i = from + 1; i <= index; i++) {
      const actual = await shell.run(this.calls[i].command); // run even without onCall: `onCall?.(..., await ...)` would skip it
      onCall?.(this.calls[i], actual);
    }
    return shell;
  }

  /** Runs every call on `shell` (default: a fresh one at the start) and lists how each result differs from the recording. */
  async replay(shell: Shell = this.createShell(), normalize: (s: string) => string = s => s): Promise<{ matches: boolean; differences: ReplayDifference[] }> {
    const differences: ReplayDifference[] = [];
    for (const call of this.calls) differences.push(...compare(call, await shell.run(call.command), normalize));
    return { matches: differences.length === 0, differences };
  }

  // ---------- the file ----------

  /**
   * The recording as a .topwater file: manifest.json is the machine after the last call (the newest checkpoint),
   * checkpoints/0.json the start, checkpoints/<i+1>.json the snapshot after call i.
   */
  async save(): Promise<Uint8Array> {
    const o = this.options;
    const header = dropUndefined({
      format: HISTORY, version: HISTORY_VERSION, created: iso(this.created), topwater: this.topwater,
      options: dropUndefined({
        user: o.user ?? this.start.user, hostname: o.hostname ?? this.start.hostname, startTime: o.startTime === undefined ? undefined : iso(o.startTime),
        randomSeed: o.randomSeed, maxLoopIterations: o.maxLoopIterations, maxExtractBytes: o.maxExtractBytes, maxExtractEntries: o.maxExtractEntries,
        maxCallDurationMs: o.maxCallDurationMs === Infinity ? -1 : o.maxCallDurationMs, maxFileSystemBytes: o.maxFileSystemBytes, maxStringBytes: o.maxStringBytes,
      }),
      appCommands: this.appCommands.length ? this.appCommands : undefined,
      metadata: Object.keys(this.metadata).length ? this.metadata : undefined,
      web: this.web && (p => ({
        domains: p.domains, methods: p.methods, allowHttp: p.allowHttp, maxResponseBytes: p.maxResponseBytes, maxRequestsPerCall: p.maxRequestsPerCall,
        maxRequests: p.maxRequests, timeoutMs: p.timeoutMs, maxRedirects: p.maxRedirects, explainBlocked: p.explainBlocked,
        credentials: Object.keys(p.credentials).sort((a, b) => a < b ? -1 : a > b ? 1 : 0),
      }))(new WebSession(this.web).policy),
    });
    const blobs = new Map<string, Uint8Array>();
    const exchange = async (x: WebExchange) => {
      if (x.error) return { method: x.method, url: x.url, error: x.error };
      const body = x.body.length ? await sha256(x.body) : undefined;
      if (body) blobs.set(body, x.body);
      return { method: x.method, url: x.url, status: x.status, reason: x.reason, headers: x.headers, body, size: x.body.length };
    };
    const lines = [JSON.stringify(header)];
    const checkpoints: [string, ShellSnapshot][] = [['checkpoints/0.json', this.start]];
    for (const [i, c] of this.calls.entries()) {
      lines.push(JSON.stringify(dropUndefined({
        index: c.index, command: c.command, stdout: c.stdout, stderr: c.stderr, exitCode: c.exitCode, trailer: c.trailer,
        elapsedMs: Math.round(c.elapsedMs * 1000) / 1000, time: c.time === undefined ? undefined : iso(c.time), files: c.files?.length ? c.files : undefined,
        web: c.web?.length ? await Promise.all(c.web.map(exchange)) : undefined,
      })));
      if (c.snapshot) checkpoints.push([`checkpoints/${i + 1}.json`, c.snapshot]);
    }
    return writeTopwater([['manifest.json', checkpoints[checkpoints.length - 1][1]], ['history.jsonl', lines.join('\n') + '\n'], ...checkpoints], blobs);
  }

  /**
   * Reads a .topwater file (by its zip signature; one without history is a recording of no calls) or a JSON Lines
   * recording of the format this replaced, from either port. Everything in it is checked as untrusted: each machine like
   * ShellSnapshot.load, under `limits` or the defaults, never limits the file names; the replay shell's limits are the
   * file's clamped to the same bound, so a recording can lower limits but never raise them. `openSnapshot` opens an old
   * recording's side-car snapshot by the relative path it names, which is only ever segments of letters, digits, `.`,
   * `_` and `-` separated by `/` (none of only dots); the opener must treat it as nothing else.
   */
  static async load(input: string | Uint8Array, openSnapshot?: (path: string) => Promise<Uint8Array>, limits: RecordedOptions = {}): Promise<Recording> {
    if (typeof input !== 'string' && input[0] === 0x50 && input[1] === 0x4b) return loadTopwater(input, limits);
    // not compressed, so as large as what the sender sends: the cap of a .topwater (UTF-8 is never shorter than the string)
    const b = { ...DEFAULTS, ...limits };
    const max = 4 * (2 * b.maxFileSystemBytes + 4 * b.maxStringBytes + 1024 * b.maxExtractEntries);
    if (input.length > max) throw bad(`the file is larger than ${max} bytes`);
    // each snapshot is capped by the limits; these cap them all together
    const total = totals(limits);
    const read = async (r: any, what: string): Promise<ShellSnapshot> => {
      if (typeof r?.embedded === 'string') {
        let bytes: Uint8Array;
        try { bytes = fromBase64(r.embedded); } catch { throw bad(`${what}: the embedded snapshot is not base64`); }
        return total(await ShellSnapshot.load(bytes, limits));
      }
      if (typeof r?.file === 'string') {
        if (!openSnapshot) throw bad(`${what} is in a side-car file (${r.file}); open it together with the recording`);
        if (!r.file.split('/').every((s: string) => /^[A-Za-z0-9._-]+$/.test(s) && !/^\.+$/.test(s)))
          throw bad(`snapshot path ${JSON.stringify(r.file)} is not a plain relative path inside the recording's directory`);
        return total(await ShellSnapshot.load(await openSnapshot(r.file), limits));
      }
      throw bad(`${what} has no snapshot`);
    };
    return fromLines(input, FORMAT, RECORDING_FORMAT_VERSION, limits, h => read(h.start, 'the start'), e => e.snapshot ? read(e.snapshot, `call ${e.index}`) : undefined,
      (_blob, _size, where) => { throw bad(`${where}: web responses need a .topwater file`); });
  }
}

/** Counts a recording's snapshots and their files' bytes (`held`, or else counted from its entries) against the caps for all of them together. */
function totals(limits: RecordedOptions) {
  let snapshots = 0, bytes = 0;
  const max = MAX_SNAPSHOTS_FILE_SYSTEMS * (limits.maxFileSystemBytes ?? DEFAULTS.maxFileSystemBytes);
  return (s: ShellSnapshot, held?: number) => {
    if (++snapshots > MAX_SNAPSHOTS) throw bad(`more than ${MAX_SNAPSHOTS} snapshots`);
    if (held !== undefined) bytes += held;
    else for (const e of s.entries) bytes += e.data?.length ?? e.text?.length ?? 0;
    if (bytes > max) throw bad(`the snapshots hold more than ${max} bytes of files together (${MAX_SNAPSHOTS_FILE_SYSTEMS} x maxFileSystemBytes)`);
    return s;
  };
}

const CHECKPOINT = /^checkpoints\/(0|[1-9][0-9]{0,8})\.json$/;

async function loadTopwater(bytes: Uint8Array, limits: RecordedOptions): Promise<Recording> {
  const z = openTopwater(bytes, limits, 4);
  if (!z.has('history.jsonl')) {
    const end = await z.machine('manifest.json');
    const rec = new Recording(end, clamp({ user: end.user, hostname: end.hostname }, limits));
    rec.created = end.created;
    rec.topwater = undefined;
    return rec;
  }
  // what a session's output could plausibly be; deflate shrinks repetitive text about 1000:1, so the zip's size bounds nothing
  const history = z.bytes('history.jsonl', 2 * (limits.maxStringBytes ?? DEFAULTS.maxStringBytes));
  const checkpoints = new Set<number>();
  const total = totals(limits);
  const budget = { entries: 4 * (limits.maxExtractEntries ?? DEFAULTS.maxExtractEntries) };
  const kept: ShellSnapshot[] = []; // the few checkpoints built at a time
  const wrap = (n: number) => (e: unknown) => e instanceof SnapshotError ? bad(`checkpoints/${n}.json: ${e.message.replace(/^not a valid Topwater snapshot: /, '')}`) : e;
  // the start is built now; the others are checked against the limits now and built when a replay needs one
  const checkpoint = async (n: number) => {
    try {
      if (n === 0) return total(await z.machine('checkpoints/0.json', budget));
      const { snapshot, held } = await z.outline(`checkpoints/${n}.json`, budget, kept, wrap(n));
      return total(snapshot, held);
    } catch (e) { throw wrap(n)(e); }
  };
  // runs once the header's version is known to be one this reads
  const start = () => {
    for (const name of z.names) {
      if (!name.startsWith('checkpoints/')) continue;
      const m = CHECKPOINT.exec(name);
      if (!m) throw bad(`${name} is not a checkpoint name`);
      checkpoints.add(Number(m[1]));
    }
    if (checkpoints.size > MAX_SNAPSHOTS) throw bad(`more than ${MAX_SNAPSHOTS} checkpoints`);
    if (!checkpoints.has(0)) throw bad('checkpoints/0.json (the start) is missing');
    return checkpoint(0);
  };
  let calls = 0, webBytes = 0;
  const maxWeb = limits.maxFileSystemBytes ?? DEFAULTS.maxFileSystemBytes;
  const blob = async (hash: unknown, size: number, where: string) => {
    // every response body together within one machine's worth of files
    if ((webBytes += size) > maxWeb) throw bad(`${where}: the recorded responses hold more than ${maxWeb} bytes (MaxFileSystemBytes)`);
    if (size === 0) return new Uint8Array(0);
    if (typeof hash !== 'string' || !/^[0-9a-f]{64}$/.test(hash)) throw bad(`${where}: bad blob ${JSON.stringify(hash ?? null)}`);
    if (!z.has('blobs/' + hash)) throw bad(`${where}: blob ${hash} is missing`);
    let b: Uint8Array;
    try { b = z.bytes('blobs/' + hash, size); } catch { throw bad(`${where}: blob ${hash} doesn't hold the ${size} bytes it should`); }
    if (b.length !== size || await sha256(b) !== hash) throw bad(`${where}: blob ${hash} doesn't hold the ${size} bytes it should`);
    return b;
  };
  const rec = await fromLines(history, HISTORY, HISTORY_VERSION, limits, start, () => { calls++; return checkpoints.has(calls) ? checkpoint(calls) : undefined; }, blob);
  const beyond = [...checkpoints].find(n => n > calls);
  if (beyond !== undefined) throw bad(`checkpoints/${beyond}.json is after the last call`);
  // the machine of the file is the one after the last checkpoint: a reader of either must see the same
  const last = `checkpoints/${Math.max(...checkpoints)}.json`;
  if (!z.has('manifest.json')) throw bad('manifest.json is missing');
  const max = 4 * (limits.maxStringBytes ?? DEFAULTS.maxStringBytes) + 1024 * (limits.maxExtractEntries ?? DEFAULTS.maxExtractEntries);
  if (!sameJson(z.bytes('manifest.json', max), z.bytes(last, max))) throw bad(`manifest.json is not the machine of ${last}, the last checkpoint`);
  return rec;
}

function sameJson(a: Uint8Array, b: Uint8Array): boolean {
  if (a.length === b.length && a.every((x, i) => x === b[i])) return true;
  const text = (x: Uint8Array) => { try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(x)); } catch { return undefined; } };
  const same = (x: unknown, y: unknown): boolean => {
    if (x === y) return true;
    if (typeof x !== 'object' || typeof y !== 'object' || x === null || y === null || Array.isArray(x) !== Array.isArray(y)) return false;
    const kx = Object.keys(x), ky = Object.keys(y);
    return kx.length === ky.length && kx.every(k => Object.hasOwn(y, k) && same((x as any)[k], (y as any)[k]));
  };
  const x = text(a);
  return x !== undefined && same(x, text(b));
}

/** The lines of a text, or of UTF-8 bytes, one at a time: the text may be large, so never all of them at once. */
function* linesOf(input: string | Uint8Array): Generator<string> {
  const utf8 = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }); // a BOM is kept, so it fails as JSON like in the other ports
  for (let at = 0, n = 1; at < input.length; n++) {
    let end = typeof input === 'string' ? input.indexOf('\n', at) : input.indexOf(0x0a, at);
    if (end < 0) end = input.length;
    if (typeof input === 'string') yield input.slice(at, end);
    else try { yield utf8.decode(input.subarray(at, end)); } catch { throw bad(`line ${n} is not UTF-8`); }
    at = end + 1;
  }
}

/** The recording in `input`: a header line, then one line per call (the old format and history.jsonl alike). */
async function fromLines(input: string | Uint8Array, format: string, version: number, limits: RecordedOptions,
  start: (header: Record<string, any>) => Promise<ShellSnapshot>, after: (line: Record<string, any>) => Promise<ShellSnapshot> | undefined,
  blob: (hash: unknown, size: number, where: string) => Promise<Uint8Array>): Promise<Recording> {
  const lines = linesOf(input);
  const first = lines.next().value ?? '';
  let h: Record<string, any>;
  try { h = JSON.parse(first); } catch { throw bad(first.trim() ? 'the header is not JSON' : 'the file is empty'); }
  if (!h || typeof h !== 'object' || h.format !== format) throw bad(`the first line is not a ${format} header`);
  if (typeof h.version !== 'number' || h.version < 1) throw bad(`unknown format version ${h.version}`);
  if (h.version > version)
    throw new Error(`${format === FORMAT ? 'recording' : 'history'} format version ${h.version} is newer than this version of Topwater supports (${version}); upgrade Topwater to read it`);
  const o = h.options;
  if (!o || typeof o !== 'object') throw bad('options are missing');
  const int = (v: unknown, what: string, range = 2 ** 53) => { if (v == null) return undefined; if (!(Number.isSafeInteger(v) && (v as number) >= -range && (v as number) < range)) throw bad(`bad ${what}`); return v as number; };
  const name = (v: unknown, shape: RegExp, what: string) => { if (v == null) return undefined; if (typeof v !== 'string' || !shape.test(v)) throw bad(`bad ${what} ${JSON.stringify(v)}`); return v; };
  const options: RecordedOptions = {
    user: name(o.user, USER, 'user'), hostname: name(o.hostname, HOST, 'hostname'),
    startTime: o.startTime == null ? undefined : time(o.startTime, 'startTime'), randomSeed: int(o.randomSeed, 'randomSeed', 2 ** 31),
    maxLoopIterations: int(o.maxLoopIterations, 'maxLoopIterations'), maxExtractBytes: int(o.maxExtractBytes, 'maxExtractBytes'),
    maxExtractEntries: int(o.maxExtractEntries, 'maxExtractEntries'), maxCallDurationMs: o.maxCallDurationMs === -1 ? Infinity : int(o.maxCallDurationMs, 'maxCallDurationMs'),
    maxFileSystemBytes: int(o.maxFileSystemBytes, 'maxFileSystemBytes'), maxStringBytes: int(o.maxStringBytes, 'maxStringBytes'),
  };
  for (const k of Object.keys(options) as (keyof RecordedOptions)[]) if (options[k] === undefined) delete options[k];
  const rec = new Recording(await start(h), clamp(options, limits));
  rec.created = h.created == null ? 0 : time(h.created, 'created');
  rec.topwater = typeof h.topwater === 'string' ? h.topwater : undefined;
  if (h.metadata && typeof h.metadata === 'object') for (const [k, v] of Object.entries(h.metadata)) if (typeof v === 'string') rec.metadata[k] = v;
  if (Array.isArray(h.appCommands)) rec.appCommands = h.appCommands.filter((c: unknown): c is string => typeof c === 'string');
  if (h.web != null) rec.web = webPolicy(h.web);
  const maxCalls = limits.maxExtractEntries ?? DEFAULTS.maxExtractEntries;
  let n = 1;
  for (const line of lines) {
    // every line counts, blank ones too: a file of nothing but newlines is still bounded
    if (++n > maxCalls + 1) throw bad(`more than ${maxCalls} calls (MaxExtractEntries)`);
    if (!line.trim()) continue;
    let e: Record<string, any>;
    try { e = JSON.parse(line); } catch { throw bad(`line ${n} is not JSON`); }
    if (!e || typeof e.command !== 'string') throw bad(`line ${n} has no command`);
    if (e.index != null && !(Number.isSafeInteger(e.index) && e.index >= 0)) throw bad(`line ${n}: bad index`);
    if (e.elapsedMs != null && !(typeof e.elapsedMs === 'number' && Number.isFinite(e.elapsedMs) && e.elapsedMs >= 0 && e.elapsedMs <= 1e12)) throw bad(`line ${n}: bad elapsedMs`);
    if (e.files != null && !(Array.isArray(e.files) && e.files.every((f: unknown) => typeof f === 'string'))) throw bad(`line ${n}: files is not a list of paths`);
    rec.calls.push({
      index: e.index ?? rec.calls.length, command: e.command,
      stdout: typeof e.stdout === 'string' ? e.stdout : '', stderr: typeof e.stderr === 'string' ? e.stderr : '',
      exitCode: Number.isInteger(e.exitCode) ? e.exitCode : 0, trailer: typeof e.trailer === 'string' ? e.trailer : undefined,
      elapsedMs: e.elapsedMs ?? 0,
      time: e.time == null ? undefined : time(e.time, `line ${n} time`),
      files: e.files ?? undefined,
      snapshot: await after(e),
      web: e.web == null ? undefined : await exchanges(e.web, `line ${n}`, blob),
    });
  }
  return rec;
}

/** The policy in the header: everything but the credentials' values and the transport. */
function webPolicy(w: any): WebPolicy {
  if (typeof w !== 'object' || Array.isArray(w)) throw bad('bad web policy');
  const strings = (v: unknown, what: string, fallback: string[]) => {
    if (v == null) return fallback;
    if (!Array.isArray(v) || !v.every(x => typeof x === 'string')) throw bad(`bad web policy: ${what} is not a list of strings`);
    return v as string[];
  };
  const num = (v: unknown, what: string, max = 2 ** 53) => {
    if (v == null) return 0;
    if (!(Number.isSafeInteger(v) && (v as number) >= 0)) throw bad(`bad web policy: bad ${what}`);
    return Math.min(v as number, max);
  };
  const bool = (v: unknown, what: string) => { if (v != null && typeof v !== 'boolean') throw bad(`bad web policy: bad ${what}`); return v === true; };
  const policy: WebPolicy = {
    domains: strings(w.domains, 'domains', []), methods: strings(w.methods, 'methods', ['GET']), allowHttp: bool(w.allowHttp, 'allowHttp'),
    maxResponseBytes: num(w.maxResponseBytes, 'maxResponseBytes'), maxRequestsPerCall: num(w.maxRequestsPerCall, 'maxRequestsPerCall', 2 ** 31 - 1),
    maxRequests: num(w.maxRequests, 'maxRequests', 2 ** 31 - 1), timeoutMs: num(w.timeoutMs, 'timeoutMs', 2 ** 31 - 1),
    maxRedirects: num(w.maxRedirects, 'maxRedirects', 2 ** 31 - 1), explainBlocked: bool(w.explainBlocked, 'explainBlocked'),
    credentials: Object.fromEntries(strings(w.credentials, 'credentials', []).map(h => [h, () => ''])),
  };
  try { void new WebSession(policy); } catch (e) { throw bad('bad web policy: ' + (e as Error).message); }
  return policy;
}

const ERRORS = ['resolve', 'blocked', 'connect', 'timeout', 'tls', 'recv'];

/** One call's requests and responses; each body is a blob. */
async function exchanges(list: unknown, where: string, blob: (hash: unknown, size: number, where: string) => Promise<Uint8Array>): Promise<WebExchange[]> {
  if (!Array.isArray(list)) throw bad(`${where}: web is not a list`);
  const out: WebExchange[] = [];
  for (const [i, x] of list.entries()) {
    const w = `${where} web ${i}`;
    if (x == null || typeof x !== 'object') throw bad(`${where}: null in web`);
    if (typeof x.method !== 'string' || !x.method || typeof x.url !== 'string' || !x.url) throw bad(`${w}: method and url are required`);
    if (x.error != null) {
      if (!ERRORS.includes(x.error)) throw bad(`${w}: unknown error ${JSON.stringify(x.error)}`);
      out.push({ method: x.method, url: x.url, status: 0, reason: '', headers: [], body: new Uint8Array(0), error: x.error });
      continue;
    }
    const size = x.size ?? 0;
    if (!(Number.isInteger(x.status) && x.status >= 100 && x.status <= 999) || !(Number.isSafeInteger(size) && size >= 0)
      || (x.reason != null && typeof x.reason !== 'string')
      || (x.headers != null && !(Array.isArray(x.headers) && x.headers.every((h: unknown) => Array.isArray(h) && h.length === 2 && h.every(s => typeof s === 'string')))))
      throw bad(`${w}: bad response`);
    out.push({ method: x.method, url: x.url, status: x.status, reason: x.reason ?? '', headers: x.headers ?? [], body: await blob(x.body, size, w) });
  }
  return out;
}

/** The file's limits, never above the caller's (or the defaults). */
function clamp(options: RecordedOptions, limits: RecordedOptions): RecordedOptions {
  const bound = { ...DEFAULTS, ...limits };
  for (const k of LIMITS) options[k] = Math.min(Math.max(options[k] ?? bound[k], 0), bound[k]);
  return options;
}

export interface RecordingOptions {
  metadata?: Record<string, string>;
  /** take a snapshot after every Nth call (0: never) */
  snapshotEvery?: number;
}

/** Records every `shell.run` call until `stop()`: the machine now is the start. */
export function startRecording(shell: Shell, options: RecordingOptions = {}) {
  const { fileSystem: _, ...shellOptions } = shell.options;
  const recording = new Recording(ShellSnapshot.capture(shell), shellOptions);
  recording.metadata = { ...options.metadata };
  recording.appCommands = [...shell.commands.keys()].filter(n => !shell.isBuiltin(n)).sort();
  const every = options.snapshotEvery ?? 0;
  const run = shell.run;
  const changes = shell.fs.trackChanges(); // the paths each call changed, without a snapshot per call
  shell.run = async (command, onOutput) => {
    const time = shell.now(), t0 = performance.now();
    const r = await run.call(shell, command, onOutput);
    const index = recording.calls.length;
    const last = recording.calls[index - 1];
    if (last && !(every > 0 && index % every === 0)) last.snapshot = undefined;
    // the last call always has a snapshot, the end state; it is taken when first used (or at stop()), since the next call
    // usually replaces it.
    // shortcut: a change made to the shell outside run() after the last call, before then, is in it too; capture every call again if a host needs that
    const keep = every > 0 && (index + 1) % every === 0;
    const snapshot = keep ? ShellSnapshot.capture(shell) : ShellSnapshot.lazy(() => ShellSnapshot.capture(shell), shell.options.user, shell.options.hostname, shell.now());
    const files = changes();
    recording.web ??= shell.web?.policy;
    const web = shell.web?.takeLog();
    recording.calls.push({
      index, command, stdout: r.stdout, stderr: r.stderr, exitCode: r.exitCode, trailer: r.trailer, elapsedMs: performance.now() - t0,
      time, files: files.length ? files : undefined, snapshot, web: web?.length ? web : undefined,
    });
    return r;
  };
  return {
    recording,
    stop: () => {
      if (shell.run === run) return;
      shell.run = run;
      void recording.calls.at(-1)?.snapshot?.entries; // the end state, before the shell moves on
    },
  };
}

/** @internal Paths whose entry was added, removed or changed between two captures, sorted ordinally. */
export function changedPaths(a: readonly VfsEntry[], b: readonly VfsEntry[]): string[] {
  const old = new Map(a.map(e => [e.path, e]));
  const out: string[] = [];
  for (const e of b) {
    const o = old.get(e.path);
    old.delete(e.path);
    if (!o || !sameEntry(o, e)) out.push(e.path);
  }
  out.push(...old.keys());
  return out.sort((x, y) => x < y ? -1 : x > y ? 1 : 0);
}

function compare(c: RecordedCall, a: ShellResult, n: (s: string) => string): ReplayDifference[] {
  const out: ReplayDifference[] = [];
  const add = (field: ReplayDifference['field'], e: string, x: string) => { if (n(e) !== n(x)) out.push({ index: c.index, command: c.command, field, expected: e, actual: x }); };
  add('exitCode', String(c.exitCode), String(a.exitCode));
  add('stdout', c.stdout, a.stdout);
  add('stderr', c.stderr, a.stderr);
  add('trailer', c.trailer ?? '', a.trailer ?? '');
  return out;
}

const iso = (ms: number) => new Date(ms).toISOString().replace(/\.(\d{3})Z$/, '.$10000Z');
function time(s: unknown, what: string): number {
  try { return parseTime(s, what); } catch { throw bad(`bad ${what} ${JSON.stringify(s)}`); }
}

function fromBase64(b64: string): Uint8Array {
  const s = atob(b64);
  const out = new Uint8Array(s.length);
  for (let i = 0; i < s.length; i++) out[i] = s.charCodeAt(i);
  return out;
}

function dropUndefined<T extends Record<string, unknown>>(o: T): T {
  for (const k of Object.keys(o)) if (o[k] === undefined) delete o[k];
  return o;
}
