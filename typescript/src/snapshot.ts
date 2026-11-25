// Snapshots in the C# port's saved format (docs/reference.md "Snapshots"): a zip with manifest.json and blobs/<sha256>.
// A snapshot saved by either port loads in the other. Loading treats the bytes as hostile, with the same limits as C#.
import { readZip, writeZip, crc32, dosTime, ZipFormatError } from './commands/zipfile.js';
import { inflateRaw } from './inflate.js';
import { deflate } from './compress.js';
import { parse, ParseError, type Script } from './parser.js';
import type { Shell, ShellOptions } from './shell.js';
import type { VfsEntry } from './vfs.js';

export const SNAPSHOT_FORMAT_VERSION = 1;
const FORMAT = 'topwater-snapshot';
const enc = new TextEncoder();

/** The session part of a snapshot, as both ports store it. @internal */
export interface ShellStateData {
  cwd: string;
  variables: Map<string, string>;
  attributes: Map<string, string>;
  unexported: Set<string>;
  arrays: Map<string, { assoc: boolean; next: number; items: [string, string][] }>;
  /** name -> body as written (`{ ...; }`) */
  functions: Map<string, string>;
  exportedFunctions: Set<string>;
  aliases: Map<string, string>;
  traps: Map<string, string>;
  setOptions: string[];
  ifsUnset: boolean;
  shopts: string[];
  positional: string[];
  dirStack: string[];
  history: string[];
  lastExitCode: number;
  secondsBase: number;
  lastArg: string;
  pipeStatus: number[];
  getopts: [number, number];
  jobs: [number, number][];
  lastBackgroundPid: number;
  nextPid: number;
  /** the virtual clock (ms) and the seeded generator's state, for a deterministic shell */
  clock?: number;
  random?: bigint;
}

/** The `set -o` options a snapshot may name. */
export const SNAPSHOT_SET_OPTIONS = new Set(['errexit', 'pipefail', 'nounset', 'noglob', 'xtrace', 'noclobber']);

export class SnapshotError extends Error {
  constructor(why: string) { super('not a valid Topwater snapshot: ' + why); }
}
const bad = (why: string) => new SnapshotError(why);

/** A whole machine at one moment: every file and the session. Immutable once made; restore it into any shell. */
export class ShellSnapshot {
  private constructor(
    /** @internal */ readonly files: readonly VfsEntry[],
    /** @internal */ readonly state: ShellStateData,
    readonly user: string,
    readonly hostname: string,
    /** when it was taken, ms since the epoch (the virtual clock for a deterministic shell) */
    readonly created: number,
  ) {}

  /** Captures a shell's files and session. */
  static capture(shell: Shell): ShellSnapshot {
    return new ShellSnapshot([...shell.fs.entries()].map(e => ({ ...e })), shell.captureState(), shell.options.user, shell.options.hostname, shell.now());
  }

  /** Puts the machine back as captured. The shell keeps its own registered commands (and /usr/bin entries for them). */
  restore(shell: Shell) {
    shell.fs.replaceAll(this.files);
    shell.applyState(this.state);
  }

  /** The files, parents first: [path, isDirectory, mode, owner, mtime ms, text or bytes]. */
  get entries(): readonly VfsEntry[] { return this.files; }

  // ---------- saving ----------

  /** The snapshot as the C# port's zip: manifest.json plus one blob per distinct content. */
  async save(): Promise<Uint8Array> {
    const zip: Parameters<typeof writeZip>[0] = [];
    const [time, date] = dosTime(Date.now());
    const add = async (name: string, raw: Uint8Array) =>
      zip.push({ name, method: 8, flags: 0, crc: crc32(raw), usize: raw.length, time, date, ext: 0, data: await deflate('deflate-raw', raw) });
    const written = new Set<string>();
    const files: Record<string, unknown>[] = [];
    for (const e of this.files) {
      const entry: Record<string, unknown> = { path: e.path, type: e.isDirectory ? 'dir' : 'file', mode: e.mode.toString(8).padStart(4, '0'), owner: e.owner, mtime: formatTime(e.modified) };
      if (!e.isDirectory) {
        const bytes = e.data ?? enc.encode(e.text ?? '');
        entry.size = bytes.length;
        if (e.data) entry.binary = true;
        if (e.reportedSize != null) entry.reportedSize = e.reportedSize;
        if (bytes.length > 0) {
          const blob = await sha256(bytes);
          entry.blob = blob;
          if (!written.has(blob)) { written.add(blob); await add('blobs/' + blob, bytes); }
        }
      }
      files.push(entry);
    }
    const st = this.state;
    const names = [...new Set([...st.variables.keys(), ...st.arrays.keys(), ...st.attributes.keys()])].sort(ordinal);
    const manifest: Record<string, unknown> = {
      format: FORMAT, version: SNAPSHOT_FORMAT_VERSION, created: formatTime(this.created), user: this.user, hostname: this.hostname, cwd: st.cwd, files,
      variables: names.map(n => dropEmpty({
        name: n, value: st.variables.get(n), attrs: st.attributes.get(n), exported: isExported(st, n),
        array: st.arrays.has(n) ? (a => ({ assoc: a.assoc || undefined, next: a.next || undefined, items: a.items }))(st.arrays.get(n)!) : undefined,
      })),
      functions: [...st.functions].map(([name, body]) => dropEmpty({ name, body, exported: st.exportedFunctions.has(name) })),
      aliases: Object.fromEntries(st.aliases), set: st.setOptions, ifsUnset: st.ifsUnset || undefined, shopt: st.shopts, traps: Object.fromEntries(st.traps),
      positional: st.positional, dirStack: st.dirStack, history: st.history, lastExitCode: st.lastExitCode || undefined,
      secondsBase: formatTime(st.secondsBase), lastArg: st.lastArg || undefined, pipeStatus: st.pipeStatus, getopts: st.getopts, jobs: st.jobs,
      lastBackgroundPid: st.lastBackgroundPid || undefined, nextPid: st.nextPid || undefined,
      clock: st.clock === undefined ? undefined : formatTime(st.clock), random: st.random?.toString(),
    };
    await add('manifest.json', enc.encode(JSON.stringify(dropEmpty(manifest))));
    return writeZip(zip.reverse()); // manifest first, as C# readers expect nothing in particular but humans like it
  }

  // ---------- loading ----------

  /**
   * Reads a snapshot saved by either port, treating it as untrusted: `limits`' maxFileSystemBytes, maxStringBytes and
   * maxExtractEntries bound it (the defaults otherwise), and every path, name, hash and size is checked. Throws
   * SnapshotError for anything malformed, Error("... newer ...") for a newer format version.
   */
  static async load(bytes: Uint8Array, limits: Partial<ShellOptions> = {}): Promise<ShellSnapshot> {
    const o = { maxFileSystemBytes: 128 << 20, maxStringBytes: 32 << 20, maxExtractEntries: 100_000, ...limits };
    try { return await read(bytes, o); }
    catch (e) {
      if (e instanceof SnapshotError || e instanceof SnapshotVersionError) throw e;
      throw bad(e instanceof Error ? e.message : String(e));
    }
  }
}

export class SnapshotVersionError extends Error {}

const USER = /^[A-Za-z0-9_][A-Za-z0-9_.-]{0,31}$/, HOST = /^[A-Za-z0-9][A-Za-z0-9.-]{0,63}$/, VAR = /^[A-Za-z_][A-Za-z0-9_]*$/;
const WORD = /^[A-Za-z0-9_+-]{1,32}$/, BLOB = /^[0-9a-f]{64}$/;
const check = (v: unknown, shape: RegExp, what: string): string => {
  if (typeof v === 'string' && shape.test(v)) return v;
  throw bad(`bad ${what} ${JSON.stringify(v)}`);
};

async function read(bytes: Uint8Array, o: { maxFileSystemBytes: number; maxStringBytes: number; maxExtractEntries: number }): Promise<ShellSnapshot> {
  const maxManifest = 4 * o.maxStringBytes + 1024 * o.maxExtractEntries;
  const maxArchive = 2 * o.maxFileSystemBytes + maxManifest;
  if (bytes.length > maxArchive) throw bad(`the snapshot is larger than ${maxArchive} bytes`);
  let entries;
  try { entries = readZip(bytes); } catch (e) { throw bad(e instanceof ZipFormatError ? e.message : 'not a zip file'); }
  if (entries.length > o.maxExtractEntries + 1) throw bad(`more than ${o.maxExtractEntries} entries (MaxExtractEntries)`);
  const byName = new Map(entries.map(e => [e.name, e]));
  const open = (name: string, max: number, what: string) => {
    const e = byName.get(name)!;
    if (e.method === 0) { if (e.data.length > max) throw bad(`${what} is larger than ${max} bytes`); return e.data; }
    if (e.method !== 8) throw bad(`${what}: unsupported compression`);
    try { return inflateRaw(e.data, max); } catch { throw bad(`${what} is larger than ${max} bytes or corrupt`); }
  };
  if (!byName.has('manifest.json')) throw bad('manifest.json is missing');
  let m: Record<string, any>;
  try { m = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(open('manifest.json', maxManifest, 'manifest.json'))); }
  catch (e) { throw e instanceof SnapshotError ? e : bad('manifest.json is not JSON'); }
  if (!m || typeof m !== 'object' || m.format !== FORMAT) throw bad('manifest.json is not a Topwater snapshot manifest');
  if (typeof m.version !== 'number') throw bad('no format version');
  if (m.version > SNAPSHOT_FORMAT_VERSION)
    throw new SnapshotVersionError(`snapshot format version ${m.version} is newer than this version of Topwater supports (${SNAPSHOT_FORMAT_VERSION}); upgrade Topwater to load it`);
  if (m.version < 1) throw bad(`unknown format version ${m.version}`);

  const user = check(m.user, USER, 'user'), hostname = check(m.hostname, HOST, 'hostname');
  const fileList: any[] = Array.isArray(m.files) ? m.files : m.files == null ? [] : (() => { throw bad('files is not a list'); })();
  if (fileList.length > o.maxExtractEntries) throw bad(`more than ${o.maxExtractEntries} files and directories (MaxExtractEntries)`);
  let total = 0;
  for (const f of fileList) {
    if (f && typeof f === 'object' && f.size == null) f.size = 0; // C# leaves zero sizes out
    if (f?.type === 'file') { if (!(Number.isSafeInteger(f.size) && f.size >= 0)) throw bad(`${f.path}: bad size`); total += f.size; }
    if (total > o.maxFileSystemBytes) throw bad(`the files hold more than ${o.maxFileSystemBytes} bytes (MaxFileSystemBytes)`);
  }
  const files: VfsEntry[] = [];
  const seen = new Set<string>(), dirs = new Set<string>(['/']);
  const cache = new Map<string, string | Uint8Array>();
  const strict = new TextDecoder('utf-8', { fatal: true });
  for (const f of fileList) {
    const path = f?.path;
    if (typeof path !== 'string' || path.length === 0 || path.length > 4096 || path[0] !== '/' || path.includes('\0') || canonical(path) !== path)
      throw bad(`bad path ${JSON.stringify(path)}`);
    if (seen.has(path)) throw bad(`${path} appears twice`);
    seen.add(path);
    if (path !== '/') {
      const parent = path.slice(0, path.lastIndexOf('/')) || '/';
      if (!dirs.has(parent)) throw bad(`${path}: its parent ${parent} must come first`);
    }
    if (typeof f.mode !== 'string' || !/^[0-7]{1,4}$/.test(f.mode)) throw bad(`${path}: bad mode`);
    const owner = check(f.owner, USER, `${path}: owner`);
    const modified = parseTime(f.mtime, `${path}: mtime`);
    let text: string | null = null, data: Uint8Array | null = null;
    if (f.type === 'file') {
      if (f.reportedSize != null && !(Number.isSafeInteger(f.reportedSize) && f.reportedSize >= 0)) throw bad(`${path}: negative reportedSize`);
      if (f.size === 0) { if (f.binary) data = new Uint8Array(0); else text = ''; }
      else {
        const blob = check(f.blob, BLOB, `${path}: blob`);
        const key = blob + (f.binary ? 'b' : 't');
        let content = cache.get(key);
        if (content === undefined) {
          if (!byName.has('blobs/' + blob)) throw bad(`${path}: blob ${blob} is missing`);
          const b = open('blobs/' + blob, f.size, 'blob ' + blob);
          if (b.length !== f.size) throw bad(`${path}: blob ${blob} holds ${b.length} bytes, not ${f.size}`);
          if (await sha256(b) !== blob) throw bad(`${path}: blob ${blob} doesn't match its hash`);
          try { content = f.binary ? b.slice() : strict.decode(b); } catch { throw bad(`${path}: text is not valid UTF-8`); }
          cache.set(key, content);
        }
        if (f.binary) data = content as Uint8Array; else text = content as string;
      }
    } else if (f.type !== 'dir') throw bad(`${path}: unknown type ${JSON.stringify(f.type)}`);
    if (f.type === 'dir') dirs.add(path);
    else if (path === '/') throw bad('/ is not a directory');
    files.push({ path, isDirectory: f.type === 'dir', mode: parseInt(f.mode, 8), owner, modified, text, data, reportedSize: f.type === 'file' ? f.reportedSize ?? null : null });
  }
  return new (ShellSnapshot as any)(files, readState(m, o), user, hostname, parseTime(m.created, 'created'));
}

function readState(m: Record<string, any>, o: { maxStringBytes: number }): ShellStateData {
  let chars = 0;
  const charge = (what: string, n: number) => { if ((chars += n) > o.maxStringBytes) throw bad(`${what}: variables hold more than ${o.maxStringBytes} characters (MaxStringBytes)`); };
  const list = (v: unknown, what: string): any[] => v == null ? [] : Array.isArray(v) ? v : (() => { throw bad(`${what} is not a list`); })();
  const strings = (v: unknown, what: string): string[] => list(v, what).map(s => typeof s === 'string' ? s : (() => { throw bad(`null in ${what}`); })());
  const dict = (v: unknown, what: string): Map<string, string> => {
    if (v == null) return new Map();
    if (typeof v !== 'object' || Array.isArray(v)) throw bad(`${what} is not an object`);
    return new Map(Object.entries(v as Record<string, unknown>).map(([k, x]) => [k, typeof x === 'string' ? x : (() => { throw bad(`${what} ${k} has no value`); })()]));
  };
  const variables = new Map<string, string>(), attributes = new Map<string, string>(), unexported = new Set<string>();
  const arrays: ShellStateData['arrays'] = new Map();
  for (const v of list(m.variables, 'variables')) {
    const name = check(v?.name, VAR, 'variable name');
    if (variables.has(name) || arrays.has(name) || attributes.has(name)) throw bad(`variable ${name} appears twice`);
    if (typeof v.attrs === 'string' && v.attrs.length > 0) {
      if (!/^[A-Za-z]{1,16}$/.test(v.attrs)) throw bad(`${name}: bad attributes`);
      attributes.set(name, v.attrs);
    }
    if (!v.exported) unexported.add(name);
    if (typeof v.value === 'string') { charge(name, name.length + v.value.length); variables.set(name, v.value); }
    if (v.array) {
      const assoc = !!v.array.assoc, items: [string, string][] = [], keys = new Set<string>();
      let max = -1;
      for (const item of list(v.array.items, `${name} items`)) {
        if (!Array.isArray(item) || item.length !== 2 || typeof item[0] !== 'string' || typeof item[1] !== 'string') throw bad(`${name}: an element is not a [key, value] pair`);
        const [key, value] = item as [string, string];
        if (!assoc) {
          if (!/^(0|[1-9][0-9]*)$/.test(key) || !Number.isSafeInteger(Number(key))) throw bad(`${name}: bad index ${JSON.stringify(key)}`);
          max = Math.max(max, Number(key));
        }
        if (keys.has(key)) throw bad(`${name}: element ${JSON.stringify(key)} appears twice`);
        keys.add(key);
        charge(name, key.length + value.length);
        items.push([key, value]);
      }
      const next = assoc ? 0 : Number(v.array.next ?? 0);
      if (!assoc && !(Number.isSafeInteger(next) && next >= max + 1)) throw bad(`${name}: next is below the highest index`);
      arrays.set(name, { assoc, next, items });
    }
  }
  const functions = new Map<string, string>(), exportedFunctions = new Set<string>();
  let functionChars = 0;
  for (const f of list(m.functions, 'functions')) {
    if (typeof f?.name !== 'string' || !f.name || typeof f.body !== 'string') throw bad('a function without a name or body');
    if ((functionChars += f.name.length + f.body.length) > o.maxStringBytes) throw bad(`functions hold more than ${o.maxStringBytes} characters (MaxStringBytes)`);
    // the body is parsed again, as `name() body`, and must come back as exactly that one function
    let script;
    try { script = parse(`${f.name}() ${f.body}\n`); } catch (e) { throw bad(`function ${f.name}: ${e instanceof ParseError ? e.message : 'does not parse'}`); }
    if (!isOneFunction(script, f.name)) throw bad(`function ${JSON.stringify(f.name)} is not one function definition`);
    if (functions.has(f.name)) throw bad(`function ${f.name} appears twice`);
    functions.set(f.name, f.body);
    if (f.exported) exportedFunctions.add(f.name);
  }
  const aliases = dict(m.aliases, 'aliases');
  for (const k of aliases.keys()) if (!k || /[\s=/\0]/.test(k)) throw bad(`bad alias ${JSON.stringify(k)}`);
  const traps = dict(m.traps, 'traps');
  for (const k of traps.keys()) check(k, WORD, 'trap signal');
  const setOptions = strings(m.set, 'set');
  for (const s of setOptions) if (!SNAPSHOT_SET_OPTIONS.has(s)) throw bad(`unknown set option ${JSON.stringify(s)}`);
  const shopts = strings(m.shopt, 'shopt');
  for (const s of shopts) check(s, WORD, 'shopt name');
  const cwd = m.cwd;
  if (typeof cwd !== 'string' || !cwd || cwd[0] !== '/' || canonical(cwd) !== cwd) throw bad('bad cwd');
  const code = (n: unknown) => Number.isInteger(n) && (n as number) >= 0 && (n as number) <= 255;
  if (!code(m.lastExitCode ?? 0) || !list(m.pipeStatus, 'pipeStatus').every(code)) throw bad('exit codes must be 0-255');
  const g = m.getopts;
  if (g != null && !(Array.isArray(g) && g.length === 2 && g.every(x => Number.isInteger(x) && x >= 0))) throw bad('bad getopts state');
  const jobs: [number, number][] = [];
  const pids = new Set<number>();
  for (const j of list(m.jobs, 'jobs')) {
    if (!Array.isArray(j) || j.length !== 2 || !Number.isInteger(j[0]) || !code(j[1]) || pids.has(j[0])) throw bad('bad job');
    pids.add(j[0]); jobs.push([j[0], j[1]]);
  }
  let random: bigint | undefined;
  if (m.random != null) {
    if (typeof m.random !== 'string' || !/^[0-9]{1,20}$/.test(m.random) || BigInt(m.random) >= 1n << 64n) throw bad('bad random state');
    random = BigInt(m.random);
  }
  const int = (v: unknown, what: string) => { if (v == null) return 0; if (!Number.isInteger(v)) throw bad(`bad ${what}`); return v as number; };
  return {
    cwd, variables, attributes, unexported, arrays, functions, exportedFunctions, aliases, traps, setOptions, ifsUnset: !!m.ifsUnset, shopts,
    positional: strings(m.positional, 'positional'), dirStack: strings(m.dirStack, 'dirStack'), history: strings(m.history, 'history'),
    lastExitCode: m.lastExitCode ?? 0, secondsBase: parseTime(m.secondsBase, 'secondsBase'), lastArg: typeof m.lastArg === 'string' ? m.lastArg : '',
    pipeStatus: list(m.pipeStatus, 'pipeStatus'), getopts: g ? [g[0], g[1]] : [0, 0], jobs,
    lastBackgroundPid: int(m.lastBackgroundPid, 'lastBackgroundPid'), nextPid: int(m.nextPid, 'nextPid'),
    clock: m.clock == null ? undefined : parseTime(m.clock, 'clock'), random,
  };
}

function isOneFunction(script: Script, name: string): boolean {
  if (script.items.length !== 1) return false;
  const { first, rest, background } = script.items[0];
  if (background || rest.length || first.negate || first.timed || first.commands.length !== 1) return false;
  const c = first.commands[0];
  return c.k === 'func' && c.name === name;
}

export function isExported(st: ShellStateData, name: string) {
  return (st.attributes.get(name) ?? '').includes('x') || (st.variables.has(name) && !st.unexported.has(name));
}

function canonical(path: string): string {
  if (path === '/') return '/';
  const parts = path.split('/').slice(1);
  if (parts.some(p => p === '' || p === '.' || p === '..')) return '';
  return path;
}

/** UTC, ISO 8601, 100 ns digits (always 0 below the millisecond here), as C# writes it. */
export function formatTime(ms: number): string {
  const d = new Date(Math.round(ms));
  if (Number.isNaN(d.getTime())) return '0001-01-01T00:00:00.0000000Z';
  return d.toISOString().replace(/\.(\d{3})Z$/, '.$10000Z');
}

export function parseTime(s: unknown, what: string): number {
  const m = typeof s === 'string' && /^(\d{4})-(\d\d)-(\d\d)[T ](\d\d):(\d\d)(?::(\d\d)(?:\.(\d{1,7}))?)?(Z|[+-]\d\d:?\d\d)?$/.exec(s.trim());
  if (!m) throw bad(`bad ${what} ${JSON.stringify(s)}`);
  const frac = m[7] ? Number(m[7].padEnd(7, '0').slice(0, 3)) : 0;
  let ms = Date.UTC(+m[1], +m[2] - 1, +m[3], +m[4], +m[5], +(m[6] ?? 0), frac);
  if (+m[1] < 100) { const d = new Date(ms); d.setUTCFullYear(+m[1]); ms = d.getTime(); }
  if (m[8] && m[8] !== 'Z') {
    const sign = m[8][0] === '-' ? -1 : 1, hh = +m[8].slice(1, 3), mm = +m[8].slice(-2);
    ms -= sign * (hh * 60 + mm) * 60_000;
  }
  if (!Number.isFinite(ms)) throw bad(`bad ${what} ${JSON.stringify(s)}`);
  return ms;
}

async function sha256(data: Uint8Array): Promise<string> {
  const h = new Uint8Array(await crypto.subtle.digest('SHA-256', data as never));
  let s = '';
  for (const b of h) s += b.toString(16).padStart(2, '0');
  return s;
}

function dropEmpty<T extends Record<string, unknown>>(o: T): T {
  for (const k of Object.keys(o)) {
    const v = o[k];
    if (v === undefined || v === false || (Array.isArray(v) && v.length === 0) || (v && typeof v === 'object' && !Array.isArray(v) && !(v instanceof Uint8Array) && Object.keys(v).length === 0))
      delete o[k];
  }
  return o;
}

const ordinal = (a: string, b: string) => a < b ? -1 : a > b ? 1 : 0;
