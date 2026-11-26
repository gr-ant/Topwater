// Snapshots as .topwater files (spec/topwater-file): a zip with manifest.json and blobs/<sha256>; a recording adds history
// and checkpoints to the same zip. A file saved by any port loads in the others. Loading treats the bytes as hostile, with
// the same limits in every port.
import { readZip, writeZip, crc32, dosTime, ZipFormatError, type ZipEntry } from './commands/zipfile.js';
import { inflateRaw } from './inflate.js';
import { deflate, inflateRawNative } from './compress.js';
import { parse, ParseError, type Script } from './parser.js';
import type { Shell, ShellOptions } from './shell.js';
import type { VfsEntry } from './vfs.js';

/** The newest format version this writes and loads. Version 2 added symbolic links (type "symlink" with a "target") and hard
 * links (a "link" naming the entry's first path); a snapshot without links is still written as version 1. */
export const SNAPSHOT_FORMAT_VERSION = 2;
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
    private _files: readonly VfsEntry[] | undefined,
    private _state: ShellStateData | undefined,
    readonly user: string,
    readonly hostname: string,
    /** when it was taken, ms since the epoch (the virtual clock for a deterministic shell) */
    readonly created: number,
    private load?: () => ShellSnapshot,
    private kept?: ShellSnapshot[],
  ) {}

  /**
   * @internal A snapshot whose machine `load` builds when first used: a recording's checkpoint, read from the file when a
   * replay needs it (and dropped again once `kept` holds more than a few), or the machine after a recorder's last call.
   */
  static lazy(load: () => ShellSnapshot, user: string, hostname: string, created: number, kept?: ShellSnapshot[]): ShellSnapshot {
    return new ShellSnapshot(undefined, undefined, user, hostname, created, load, kept);
  }

  /** @internal */ get files(): readonly VfsEntry[] { return this._files ?? this.build()._files!; }
  /** @internal */ get state(): ShellStateData { return this._state ?? this.build()._state!; }

  private build(): ShellSnapshot {
    const built = this.load!();
    this._files = built._files; this._state = built._state;
    if (!this.kept) this.load = undefined; // built once for good
    else {
      this.kept.push(this);
      while (this.kept.length > KEPT) { const old = this.kept.shift()!; old._files = old._state = undefined; }
    }
    return built;
  }

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

  /** The snapshot as the C# port's zip: manifest.json plus one blob per distinct content (a .topwater without history). */
  save(): Promise<Uint8Array> { return writeTopwater([['manifest.json', this]]); }

  // ---------- loading ----------

  /**
   * Reads a snapshot saved by any port, or the machine (manifest.json) of any .topwater file, treating it as untrusted: `limits`' maxFileSystemBytes, maxStringBytes and
   * maxExtractEntries bound it (the defaults otherwise), and every path, name, hash and size is checked. Throws
   * SnapshotError for anything malformed, Error("... newer ...") for a newer format version.
   */
  static async load(bytes: Uint8Array, limits: Partial<ShellOptions> = {}): Promise<ShellSnapshot> {
    // a recording may be four times a snapshot's size; its machine is bounded by the same limits
    return openTopwater(bytes, limits, 4).machine('manifest.json');
  }
}

export class SnapshotVersionError extends Error {}

/** How many lazily read checkpoints of one file stay built; older ones are read again when needed. */
const KEPT = 4;

/** @internal */
export const USER = /^[A-Za-z0-9_][A-Za-z0-9_.-]{0,31}$/, HOST = /^[A-Za-z0-9][A-Za-z0-9.-]{0,63}$/, VAR = /^[A-Za-z_][A-Za-z0-9_]*$/;
const WORD = /^[A-Za-z0-9_+-]{1,32}$/, BLOB = /^[0-9a-f]{64}$/;
const check = (v: unknown, shape: RegExp, what: string): string => {
  if (typeof v === 'string' && shape.test(v)) return v;
  throw bad(`bad ${what} ${JSON.stringify(v)}`);
};

type Limits = { maxFileSystemBytes: number; maxStringBytes: number; maxExtractEntries: number };

/** @internal Writes a zip holding the given machines (manifest.json, checkpoints) and text entries, every file content once as blobs/<sha256>. */
export async function writeTopwater(parts: [name: string, content: ShellSnapshot | string][], extraBlobs: ReadonlyMap<string, Uint8Array> = new Map()): Promise<Uint8Array> {
  const zip: ZipEntry[] = [], blobs: ZipEntry[] = [];
  const [time, date] = dosTime(Date.now());
  const add = async (to: ZipEntry[], name: string, raw: Uint8Array) =>
    to.push({ name, method: 8, flags: 0, crc: crc32(raw), usize: raw.length, time, date, ext: 0, data: await deflate('deflate-raw', raw) });
  // checkpoints share most contents (by reference): hash each once
  const known = new Map<string | Uint8Array, [hash: string, size: number]>(), written = new Set<string>();
  const content = async (e: VfsEntry) => {
    const key = e.data ?? e.text ?? '';
    let found = known.get(key);
    if (!found) {
      const bytes = e.data ?? enc.encode(e.text ?? '');
      found = [bytes.length ? await sha256(bytes) : '', bytes.length];
      if (bytes.length && !written.has(found[0])) { written.add(found[0]); await add(blobs, 'blobs/' + found[0], bytes); }
      known.set(key, found);
    }
    return found;
  };
  for (const [name, part] of parts)
    await add(zip, name, enc.encode(typeof part === 'string' ? part : JSON.stringify(await manifestOf(part, content))));
  for (const [hash, bytes] of extraBlobs) if (!written.has(hash)) { written.add(hash); await add(blobs, 'blobs/' + hash, bytes); }
  return writeZip([...zip, ...blobs]);
}

async function manifestOf(snap: ShellSnapshot, content: (e: VfsEntry) => Promise<[string, number]>): Promise<Record<string, unknown>> {
  const files: Record<string, unknown>[] = [];
  let links = false;
  for (const e of snap.files) {
    const type = e.isDirectory ? 'dir' : e.linkTarget !== null ? 'symlink' : 'file';
    const entry: Record<string, unknown> = { path: e.path, type, mode: e.mode.toString(8).padStart(4, '0'), owner: e.owner, mtime: formatTime(e.modified), target: e.linkTarget ?? undefined, link: e.hardLink ?? undefined };
    links ||= e.linkTarget !== null || e.hardLink !== null;
    if (type === 'file') {
      const [blob, size] = await content(e);
      entry.size = size;
      if (e.data) entry.binary = true;
      if (e.reportedSize != null) entry.reportedSize = e.reportedSize;
      if (size > 0) entry.blob = blob;
    }
    files.push(entry);
  }
  const st = snap.state;
  const names = [...new Set([...st.variables.keys(), ...st.arrays.keys(), ...st.attributes.keys()])].sort(ordinal);
  return dropEmpty({
    format: FORMAT, version: links ? SNAPSHOT_FORMAT_VERSION : 1, created: formatTime(snap.created), user: snap.user, hostname: snap.hostname, cwd: st.cwd, files,
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
  });
}

/**
 * @internal A .topwater (or snapshot) zip opened as untrusted input under the caller's limits (the defaults otherwise).
 * A recording may be `sizeFactor` times a snapshot's size cap. Errors are SnapshotError, or SnapshotVersionError for a newer manifest.
 */
export function openTopwater(bytes: Uint8Array, limits: Partial<ShellOptions>, sizeFactor = 1) {
  const o: Limits = { maxFileSystemBytes: 128 << 20, maxStringBytes: 32 << 20, maxExtractEntries: 100_000, ...limits };
  const maxManifest = 4 * o.maxStringBytes + 1024 * o.maxExtractEntries;
  const maxArchive = sizeFactor * (2 * o.maxFileSystemBytes + maxManifest);
  const entries = guard(() => {
    if (bytes.length > maxArchive) throw bad(`the file is larger than ${maxArchive} bytes`);
    // blobs for four file systems, the manifest, history.jsonl and up to 1000 checkpoints
    try { return readZip(bytes, 4 * o.maxExtractEntries + 1002); } catch (e) { throw bad(e instanceof ZipFormatError ? e.message : 'not a zip file'); }
  });
  const byName = new Map<string, ZipEntry>();
  // two entries of one name would let each reader pick a different one
  for (const e of entries) { if (byName.has(e.name)) throw bad(`entry ${JSON.stringify(e.name)} appears twice`); byName.set(e.name, e); }
  const open = (name: string, max: number, what: string) => {
    const e = byName.get(name)!;
    if (e.method === 0) { if (e.data.length > max) throw bad(`${what} is larger than ${max} bytes`); return e.data; }
    if (e.method !== 8) throw bad(`${what}: unsupported compression`);
    try { return inflateRaw(e.data, max); } catch { throw bad(`${what} is larger than ${max} bytes or corrupt`); }
  };
  const blobs: BlobCache = new Map();
  const hashes = new Map<string, string>(); // blob:size -> the hash of the bytes there, so a machine builds without awaiting
  const blobBytes = (blob: string, size: number) => byName.has(blob) ? open(blob, size, blob) : null;
  const parsed = (name: string, raw?: Uint8Array): Record<string, any> => {
    if (!byName.has(name)) throw bad(`${name} is missing`);
    try { return JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(raw ?? open(name, maxManifest, name))); }
    catch (e) { throw e instanceof SnapshotError ? e : bad(`${name} is not JSON`); }
  };
  /** an entry's bytes like `open`, inflated natively: a recording's checkpoints are many and large */
  const openFast = async (name: string, max: number) => {
    const e = byName.get(name);
    if (e?.method !== 8) return open(name, max, name);
    try { return await inflateRawNative(e.data, max); } catch { throw bad(`${name} is larger than ${max} bytes or corrupt`); }
  };
  /** hashes the blobs a manifest's checked file list names (anything wrong with one is reported when the machine is built) */
  const hashBlobs = async (files: any[]) => {
    for (const f of files) {
      if (!(f?.type === 'file' && f.link == null && Number.isSafeInteger(f.size) && f.size > 0 && typeof f.blob === 'string' && BLOB.test(f.blob))) continue;
      const key = `${f.blob}:${f.size}`;
      if (hashes.has(key)) continue;
      let raw: Uint8Array | null = null;
      try { raw = blobBytes('blobs/' + f.blob, f.size); } catch { /* reported when built */ }
      if (raw) hashes.set(key, await sha256(raw));
    }
  };
  const build = (m: Record<string, any>, name: string, h: Head) => readMachine(m, name, o, h, blobs, blobBytes, hashes);
  return {
    names: [...byName.keys()],
    has: (name: string) => byName.has(name),
    /** an entry's bytes, at most `max` */
    bytes: (name: string, max: number) => guard(() => open(name, max, name)),
    /** manifest.json or a checkpoint, checked like a snapshot; `budget.entries` (when given) is what all checkpoints may still hold */
    machine: (name: string, budget?: { entries: number }) => guardAsync(async () => {
      const m = parsed(name), h = head(m, name, o, budget);
      await hashBlobs(h.files);
      return build(m, name, h);
    }),
    /**
     * A checkpoint checked against the limits (and `budget`) without building it: format, version, user, hostname, time,
     * entry count and file bytes. It is built, and checked whole, when first used; `kept` holds the few that stay built.
     * Also returns the bytes its files hold once (hard links share their first name's), for the file's byte budget.
     */
    outline: (name: string, budget: { entries: number }, kept: ShellSnapshot[], wrap: (e: unknown) => unknown) => guardAsync(async () => {
      const m = parsed(name, byName.has(name) ? await openFast(name, maxManifest) : undefined), h = head(m, name, o, budget);
      const created = parseTime(m.created, 'created');
      await hashBlobs(h.files);
      const load = () => { try { const m = parsed(name); return build(m, name, head(m, name, o)); } catch (e) { throw wrap(passThrough(e)); } };
      return { snapshot: ShellSnapshot.lazy(load, h.user, h.hostname, created, kept), held: h.held };
    }),
  };
}

type BlobCache = Map<string, { content: string | Uint8Array; size: number }>;
const passThrough = (e: unknown) => e instanceof SnapshotError || e instanceof SnapshotVersionError ? e : bad(e instanceof Error ? e.message : String(e));
function guard<T>(f: () => T): T { try { return f(); } catch (e) { throw passThrough(e); } }
async function guardAsync<T>(f: () => Promise<T>): Promise<T> { try { return await f(); } catch (e) { throw passThrough(e); } }

type Head = { user: string; hostname: string; files: any[]; held: number };

/** A manifest's format, version, user, hostname, entry count and file bytes, checked before anything is built. */
function head(m: Record<string, any>, name: string, o: Limits, budget?: { entries: number }): Head {
  if (!m || typeof m !== 'object' || m.format !== FORMAT) throw bad(`${name} is not a Topwater snapshot manifest`);
  if (typeof m.version !== 'number') throw bad('no format version');
  if (m.version > SNAPSHOT_FORMAT_VERSION)
    throw new SnapshotVersionError(`snapshot format version ${m.version} is newer than this version of Topwater supports (${SNAPSHOT_FORMAT_VERSION}); upgrade Topwater to load it`);
  if (m.version < 1) throw bad(`unknown format version ${m.version}`);

  const user = check(m.user, USER, 'user'), hostname = check(m.hostname, HOST, 'hostname');
  const fileList: any[] = Array.isArray(m.files) ? m.files : m.files == null ? [] : (() => { throw bad('files is not a list'); })();
  if (fileList.length > o.maxExtractEntries) throw bad(`more than ${o.maxExtractEntries} files and directories (MaxExtractEntries)`);
  if (budget) {
    if (fileList.length > budget.entries) throw bad(`the checkpoints hold more than ${4 * o.maxExtractEntries} files and directories together (4 x MaxExtractEntries)`);
    budget.entries -= fileList.length;
  }
  let total = 0, held = 0;
  for (const f of fileList) {
    if (f && typeof f === 'object' && f.size == null) f.size = 0; // C# leaves zero sizes out
    if (f?.type === 'file') {
      if (!(Number.isSafeInteger(f.size) && f.size >= 0)) throw bad(`${f.path}: bad size`);
      total += f.size;
      if (f.link == null) held += f.size;
    }
    if (total > o.maxFileSystemBytes) throw bad(`the files hold more than ${o.maxFileSystemBytes} bytes (MaxFileSystemBytes)`);
  }
  return { user, hostname, files: fileList, held };
}

function readMachine(m: Record<string, any>, name: string, o: Limits, { user, hostname, files: fileList }: Head, cache: BlobCache,
  blobBytes: (entry: string, size: number) => Uint8Array | null, hashes: Map<string, string>): ShellSnapshot {
  const files: VfsEntry[] = [];
  const seen = new Set<string>(), dirs = new Set<string>(['/']);
  const strict = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true }); // a leading BOM is content
  for (const f of fileList) {
    const path = f?.path;
    if (typeof path !== 'string' || path.length === 0 || path.length > 4096 || path[0] !== '/' || path.includes('\0') || canonical(path) !== path)
      throw bad(`bad path ${JSON.stringify(path)}`);
    if (seen.has(path)) throw bad(`${path} appears twice`);
    seen.add(path);
    if (path !== '/') {
      const parent = path.slice(0, path.lastIndexOf('/')) || '/';
      if (!dirs.has(parent)) throw bad(seen.has(parent) ? `${path}: parent is not a directory` : `${path}: its parent ${parent} must come first`); // never through a link
    }
    if (typeof f.mode !== 'string' || !/^[0-7]{1,4}$/.test(f.mode)) throw bad(`${path}: bad mode`);
    const owner = check(f.owner, USER, `${path}: owner`);
    const modified = parseTime(f.mtime, `${path}: mtime`);
    let text: string | null = null, data: Uint8Array | null = null;
    if (f.link != null && (typeof f.link !== 'string' || f.link.length > 4096 || f.link[0] !== '/' || canonical(f.link) !== f.link || f.type === 'dir'))
      throw bad(`${path}: bad link ${JSON.stringify(f.link)}`);
    if ((f.type === 'symlink' && !(typeof f.target === 'string' && f.target.length > 0 && f.target.length <= 4096)) || (typeof f.target === 'string' && f.target.includes('\0')))
      throw bad(`${path}: bad symlink target`);
    if (f.link != null && !(seen.has(f.link) && f.link !== path && !dirs.has(f.link)))
      throw bad(`${path}: hard link to ${f.link}, which is not an earlier file`);
    if (f.type === 'file' && f.link == null) { // another name of a file shares the first one's content
      if (f.reportedSize != null && !(Number.isSafeInteger(f.reportedSize) && f.reportedSize >= 0)) throw bad(`${path}: negative reportedSize`);
      if (f.size === 0) { if (f.binary) data = new Uint8Array(0); else text = ''; }
      else {
        const blob = check(f.blob, BLOB, `${path}: blob`);
        const key = blob + (f.binary ? 'b' : 't');
        let cached = cache.get(key);
        if (cached === undefined) {
          const raw = blobBytes('blobs/' + blob, f.size);
          if (!raw) throw bad(`${path}: blob ${blob} is missing`);
          if (raw.length !== f.size) throw bad(`${path}: blob ${blob} holds ${raw.length} bytes, not ${f.size}`);
          if (hashes.get(`${blob}:${f.size}`) !== blob) throw bad(`${path}: blob ${blob} doesn't match its hash`);
          let content;
          try { content = f.binary ? raw.slice() : strict.decode(raw); } catch { throw bad(`${path}: text is not valid UTF-8`); }
          cache.set(key, cached = { content, size: raw.length });
        } else if (cached.size !== f.size) throw bad(`${path}: blob ${blob} holds ${cached.size} bytes, not ${f.size}`);
        if (f.binary) data = cached.content as Uint8Array; else text = cached.content as string;
      }
    } else if (f.type !== 'dir' && f.type !== 'symlink' && f.type !== 'file') throw bad(`${path}: unknown type ${JSON.stringify(f.type)}`);
    if (f.type === 'dir') dirs.add(path);
    else if (path === '/') throw bad('/ is not a directory');
    files.push({
      path, isDirectory: f.type === 'dir', mode: parseInt(f.mode, 8), owner, modified, text, data, reportedSize: f.type === 'file' && f.link == null ? f.reportedSize ?? null : null,
      linkTarget: f.type === 'symlink' ? f.target : null, hardLink: f.link ?? null,
    });
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

/** @internal */
export async function sha256(data: Uint8Array): Promise<string> {
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
