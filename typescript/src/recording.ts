// Recordings in the C# port's file format (docs/reference.md "Recordings"): JSON Lines, a header then one line per call,
// with snapshots embedded (base64 zip) or in side-car files. A recording made by either port loads in the other.
import { Shell, type ShellOptions, type ShellResult } from './shell.js';
import { ShellSnapshot } from './snapshot.js';

export const RECORDING_FORMAT_VERSION = 1;
const FORMAT = 'topwater-recording';

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
  /** the machine right after this call, when the recorder took a snapshot here */
  snapshot?: ShellSnapshot;
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

  constructor(public start: ShellSnapshot, public options: RecordedOptions) {}

  /** True when the run had a virtual clock and a random seed, so a replay on the same port and version must match exactly. */
  get isDeterministic() { return this.options.startTime !== undefined && this.options.randomSeed !== undefined; }

  /** A fresh shell at the recording's start (or `start`): its options, then `configure` (register your commands), then the snapshot. */
  createShell(configure?: (shell: Shell) => void, start: ShellSnapshot = this.start): Shell {
    const shell = new Shell({ ...this.options, user: start.user, hostname: start.hostname });
    configure?.(shell);
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
    const shell = this.createShell(configure, from >= 0 ? this.calls[from].snapshot : this.start);
    for (let i = from + 1; i <= index; i++) onCall?.(this.calls[i], await shell.run(this.calls[i].command));
    return shell;
  }

  /** Runs every call on `shell` (default: a fresh one at the start) and lists how each result differs from the recording. */
  async replay(shell: Shell = this.createShell(), normalize: (s: string) => string = s => s): Promise<{ matches: boolean; differences: ReplayDifference[] }> {
    const differences: ReplayDifference[] = [];
    for (const call of this.calls) differences.push(...compare(call, await shell.run(call.command), normalize));
    return { matches: differences.length === 0, differences };
  }

  // ---------- the file ----------

  /** The recording as JSON Lines with every snapshot embedded. */
  async save(): Promise<string> {
    const ref = async (s: ShellSnapshot) => ({ embedded: toBase64(await s.save()) });
    const o = this.options;
    const header = dropUndefined({
      format: FORMAT, version: RECORDING_FORMAT_VERSION, created: iso(this.created), topwater: this.topwater,
      options: dropUndefined({
        user: o.user ?? this.start.user, hostname: o.hostname ?? this.start.hostname, startTime: o.startTime === undefined ? undefined : iso(o.startTime),
        randomSeed: o.randomSeed, maxLoopIterations: o.maxLoopIterations, maxExtractBytes: o.maxExtractBytes, maxExtractEntries: o.maxExtractEntries,
        maxCallDurationMs: o.maxCallDurationMs, maxFileSystemBytes: o.maxFileSystemBytes, maxStringBytes: o.maxStringBytes,
      }),
      appCommands: this.appCommands.length ? this.appCommands : undefined,
      metadata: Object.keys(this.metadata).length ? this.metadata : undefined,
      start: await ref(this.start),
    });
    const lines = [JSON.stringify(header)];
    for (const c of this.calls)
      lines.push(JSON.stringify(dropUndefined({
        index: c.index, command: c.command, stdout: c.stdout, stderr: c.stderr, exitCode: c.exitCode, trailer: c.trailer,
        elapsedMs: Math.round(c.elapsedMs * 1000) / 1000, snapshot: c.snapshot ? await ref(c.snapshot) : undefined,
      })));
    return lines.join('\n') + '\n';
  }

  /**
   * Reads a recording saved by either port. `openSnapshot` opens a side-car snapshot by the relative path the file names.
   * Snapshots load as untrusted (see ShellSnapshot.load) under `limits` or the defaults, never limits the file names.
   */
  static async load(text: string, openSnapshot?: (path: string) => Promise<Uint8Array>, limits: RecordedOptions = {}): Promise<Recording> {
    const lines = text.split('\n');
    let h: Record<string, any>;
    try { h = JSON.parse(lines[0]); } catch { throw bad(lines[0].trim() ? 'the header is not JSON' : 'the file is empty'); }
    if (!h || typeof h !== 'object' || h.format !== FORMAT) throw bad('the first line is not a topwater-recording header');
    if (typeof h.version !== 'number' || h.version < 1) throw bad(`unknown format version ${h.version}`);
    if (h.version > RECORDING_FORMAT_VERSION)
      throw new Error(`recording format version ${h.version} is newer than this version of Topwater supports (${RECORDING_FORMAT_VERSION}); upgrade Topwater to read it`);
    const o = h.options;
    if (!o || typeof o !== 'object') throw bad('options are missing');
    const num = (v: unknown, what: string) => { if (v == null) return undefined; if (typeof v !== 'number' || !Number.isFinite(v)) throw bad(`bad ${what}`); return v; };
    const options: RecordedOptions = {
      user: typeof o.user === 'string' ? o.user : undefined, hostname: typeof o.hostname === 'string' ? o.hostname : undefined,
      startTime: o.startTime == null ? undefined : time(o.startTime, 'startTime'), randomSeed: num(o.randomSeed, 'randomSeed'),
      maxLoopIterations: num(o.maxLoopIterations, 'maxLoopIterations'), maxExtractBytes: num(o.maxExtractBytes, 'maxExtractBytes'),
      maxExtractEntries: num(o.maxExtractEntries, 'maxExtractEntries'), maxCallDurationMs: num(o.maxCallDurationMs, 'maxCallDurationMs'),
      maxFileSystemBytes: num(o.maxFileSystemBytes, 'maxFileSystemBytes'), maxStringBytes: num(o.maxStringBytes, 'maxStringBytes'),
    };
    for (const k of Object.keys(options) as (keyof RecordedOptions)[]) if (options[k] === undefined) delete options[k];
    const snap = async (r: any, what: string): Promise<ShellSnapshot> => {
      if (typeof r?.embedded === 'string') {
        let bytes: Uint8Array;
        try { bytes = fromBase64(r.embedded); } catch { throw bad(`${what}: the embedded snapshot is not base64`); }
        return ShellSnapshot.load(bytes, limits);
      }
      if (typeof r?.file === 'string') {
        if (!openSnapshot) throw bad(`${what} is in a side-car file (${r.file}); open it together with the recording`);
        if (r.file.startsWith('/') || r.file.split(/[\\/]/).includes('..')) throw bad(`snapshot path ${r.file} leaves the recording's directory`);
        return ShellSnapshot.load(await openSnapshot(r.file), limits);
      }
      throw bad(`${what} has no snapshot`);
    };
    const rec = new Recording(await snap(h.start, 'the start'), options);
    rec.created = h.created == null ? 0 : time(h.created, 'created');
    rec.topwater = typeof h.topwater === 'string' ? h.topwater : undefined;
    if (h.metadata && typeof h.metadata === 'object') for (const [k, v] of Object.entries(h.metadata)) if (typeof v === 'string') rec.metadata[k] = v;
    if (Array.isArray(h.appCommands)) rec.appCommands = h.appCommands.filter((c: unknown): c is string => typeof c === 'string');
    for (let n = 1; n < lines.length; n++) {
      if (!lines[n].trim()) continue;
      let e: Record<string, any>;
      try { e = JSON.parse(lines[n]); } catch { throw bad(`line ${n + 1} is not JSON`); }
      if (!e || typeof e.command !== 'string') throw bad(`line ${n + 1} has no command`);
      rec.calls.push({
        index: Number.isInteger(e.index) ? e.index : rec.calls.length, command: e.command,
        stdout: typeof e.stdout === 'string' ? e.stdout : '', stderr: typeof e.stderr === 'string' ? e.stderr : '',
        exitCode: Number.isInteger(e.exitCode) ? e.exitCode : 0, trailer: typeof e.trailer === 'string' ? e.trailer : undefined,
        elapsedMs: typeof e.elapsedMs === 'number' ? e.elapsedMs : 0,
        snapshot: e.snapshot ? await snap(e.snapshot, `call ${e.index}`) : undefined,
      });
    }
    return rec;
  }
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
  shell.run = async (command, onOutput) => {
    const t0 = performance.now();
    const r = await run.call(shell, command, onOutput);
    const index = recording.calls.length;
    recording.calls.push({
      index, command, stdout: r.stdout, stderr: r.stderr, exitCode: r.exitCode, trailer: r.trailer, elapsedMs: performance.now() - t0,
      snapshot: every > 0 && (index + 1) % every === 0 ? ShellSnapshot.capture(shell) : undefined,
    });
    return r;
  };
  return { recording, stop: () => { shell.run = run; } };
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
  const t = typeof s === 'string' ? Date.parse(s.replace(/(\.\d{3})\d+/, '$1')) : NaN;
  if (Number.isNaN(t)) throw bad(`bad ${what}`);
  return t;
}

function toBase64(bytes: Uint8Array): string {
  let s = '';
  for (let i = 0; i < bytes.length; i += 0x8000) s += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  return btoa(s);
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
