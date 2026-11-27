// Mounted sources: outside stores of files shown as directories in the machine (docs/mounts.md). See docs/architecture.md.
import { ShellError, VirtualFileSystem, Dir, FileNode, type Node } from './vfs.js';
import { specMessage, DeadlineExceeded, LimitExceeded, type Writer } from './command.js';
import { ControlFlow } from './shell/types.js';
import { sha1 } from './sha1.js';
import type { Shell } from './shell.js';

export type MountMode = 'read-only' | 'staged' | 'live';
export type MountCapability = 'write' | 'delete' | 'rename' | 'mkdir';
export const MOUNT_MODES: readonly MountMode[] = ['read-only', 'staged', 'live'];
const CAPABILITIES: readonly MountCapability[] = ['write', 'delete', 'rename', 'mkdir'];
const MAX_DEPTH = 32; // directories below the mount point that are listed
const MAX_NAME_BYTES = 255;
/** @internal characters in a version */
export const MAX_VERSION = 1024;
// commands that never read a file's content: nothing is fetched before them
const NO_CONTENT = new Set(['ls', 'find', 'stat', 'du', 'df', 'tree', 'cd', 'pwd', 'test', '[', 'mkdir', 'rmdir', 'rm', 'mv', 'touch',
  'mount', 'chmod', 'chown', 'chgrp', 'ln', 'realpath', 'readlink', 'basename', 'dirname', 'echo', 'printf']);

const msg = (key: string, values: Record<string, string> = {}) => specMessage('mount', key, values);
const join = (d: string, name: string) => d === '/' ? '/' + name : d + '/' + name;
const parentOf = (p: string) => VirtualFileSystem.getParent(p);
const nameOf = (p: string) => p.slice(p.lastIndexOf('/') + 1);
const why = (e: unknown) => e instanceof Error ? e.message || e.name : String(e);
/** @internal The call's time limit and other limits: never swallowed, they end the call. */
export const isControl = (e: unknown) => e instanceof DeadlineExceeded || e instanceof LimitExceeded || e instanceof ControlFlow;
const notFound = (path: string) => Object.assign(new Error(path), { code: 'ENOENT' });

// ---------- sources ----------

/** One entry a source lists: name (one path segment), isDir, size in bytes, modified (ms since the epoch, UTC, or a Date;
 * null: unknown) and version, an opaque string that changes when the entry does (an ETag, a revision, a modification time). */
export interface MountEntry {
  name: string;
  isDir: boolean;
  size?: number;
  modified?: number | Date | null;
  version?: string;
}

/** A source throws it when the expected version no longer matches what it holds. */
export class MountConflictError extends Error {}

type MaybePromise<T> = T | Promise<T>;

/** What a source implements (docs/mounts.md). Every method may return a Promise. Paths are canonical and absolute within the
 * source: "/" is its root. `type` and `id` are saved in snapshots, so they must not hold secrets; `capabilities` says which of
 * write, delete, rename and mkdir it supports (none: read-only). */
export interface MountSource {
  readonly type: string;
  readonly id: string;
  readonly capabilities: Iterable<MountCapability>;
  list(path: string): MaybePromise<MountEntry[]>;
  stat(path: string): MaybePromise<MountEntry | null>;
  read(path: string, maxBytes: number): MaybePromise<Uint8Array>;
  /** Creates the file (expectedVersion null: it must not exist) or replaces it; returns its new version. */
  write?(path: string, data: Uint8Array, expectedVersion: string | null): MaybePromise<string>;
  delete?(path: string, expectedVersion: string | null): MaybePromise<void>;
  rename?(path: string, to: string, expectedVersion: string | null): MaybePromise<void>;
  mkdir?(path: string): MaybePromise<void>;
}

const nodeFs = () => (globalThis as { process?: { getBuiltinModule?: (id: string) => any } }).process?.getBuiltinModule?.('node:fs');

/** A directory on the host (an Obsidian vault, a folder of notes); Node only. Read-only unless `writable`. Symbolic links in it
 * are left out and never followed; nothing outside `root` is reached. The version is the modification time and size. */
export class LocalFolderSource implements MountSource {
  readonly type = 'local-folder';
  readonly id: string;
  readonly capabilities: readonly MountCapability[];
  private readonly root: string;
  private readonly fs: any;

  constructor(root: string, options: { writable?: boolean; id?: string } = {}) {
    this.fs = nodeFs();
    if (!this.fs) throw new Error('LocalFolderSource needs Node (process.getBuiltinModule)');
    let real: string;
    try { real = this.fs.realpathSync(root); } catch { throw new Error(`${root}: not a directory`); }
    if (!this.fs.statSync(real).isDirectory()) throw new Error(`${root}: not a directory`);
    this.root = real.replace(/[\\/]+$/, '') || real;
    this.id = options.id ?? nameOf(this.root.replace(/\\/g, '/'));
    this.capabilities = options.writable ? CAPABILITIES : [];
  }

  private host(path: string): string {
    if (VirtualFileSystem.normalize('/', path) !== path || path.includes('\0')) throw new Error(`${path}: not a canonical path`);
    // no symbolic link on the way, the last name included: one swapped in after listing could lead out of root
    let cur = this.root;
    for (const seg of path === '/' ? [] : path.split('/').slice(1)) {
      cur += '/' + seg;
      const st = this.fs.lstatSync(cur, { throwIfNoEntry: false });
      if (!st) break;
      if (st.isSymbolicLink()) throw notFound(path);
    }
    return path === '/' ? this.root : this.root + path;
  }

  private static entry(name: string, st: any): MountEntry {
    const isDir = st.isDirectory();
    return { name, isDir, size: isDir ? 0 : Number(st.size), modified: Number(st.mtimeMs), version: `${st.mtimeNs}:${st.size}` };
  }

  private lstat(path: string): any | null {
    let st;
    try { st = this.fs.lstatSync(this.host(path), { bigint: true }); } catch (e) { if ((e as { code?: string }).code === 'ENOENT') return null; throw e; }
    return st.isDirectory() || st.isFile() ? st : null;
  }

  private check(path: string, expected: string | null) {
    const st = this.lstat(path);
    const now = st === null ? null : LocalFolderSource.entry('', st).version!;
    if (now !== expected) throw new MountConflictError(`${path}: version ${now ?? 'None'}, expected ${expected ?? 'None'}`);
  }

  list(path: string): MountEntry[] {
    const dir = this.host(path), out: MountEntry[] = [];
    for (const name of this.fs.readdirSync(dir) as string[]) {
      const st = this.fs.lstatSync(dir + '/' + name, { bigint: true });
      if (st.isDirectory() || st.isFile()) out.push(LocalFolderSource.entry(name, st));
    }
    return out;
  }

  stat(path: string): MountEntry | null {
    const st = this.lstat(path);
    return st === null ? null : LocalFolderSource.entry(nameOf(path), st);
  }

  read(path: string, maxBytes: number): Uint8Array {
    const fs = this.fs, c = fs.constants;
    const fd = fs.openSync(this.host(path), c.O_RDONLY | (c.O_NOFOLLOW ?? 0));
    try {
      if (fs.fstatSync(fd).size > maxBytes) throw new Error(msg('tooLarge'));
      const buf = new Uint8Array(Math.min(maxBytes + 1, 2 ** 31 - 1));
      let n = 0, got;
      while (n < buf.length && (got = fs.readSync(fd, buf, n, buf.length - n, null)) > 0) n += got;
      return buf.slice(0, n);
    } finally { fs.closeSync(fd); }
  }

  write(path: string, data: Uint8Array, expectedVersion: string | null): string {
    this.check(path, expectedVersion);
    const fs = this.fs, c = fs.constants, full = this.host(path);
    const tmp = `${full}.topwater-${(globalThis as { process?: { pid?: number } }).process?.pid ?? 0}.tmp`;
    const fd = fs.openSync(tmp, c.O_WRONLY | c.O_CREAT | c.O_EXCL | (c.O_NOFOLLOW ?? 0), 0o644);
    try { fs.writeSync(fd, data); } finally { fs.closeSync(fd); }
    fs.renameSync(tmp, full);
    return LocalFolderSource.entry('', fs.lstatSync(full, { bigint: true })).version!;
  }

  delete(path: string, expectedVersion: string | null) {
    const st = this.lstat(path);
    if (st === null || !st.isDirectory()) this.check(path, expectedVersion);
    if (st !== null && st.isDirectory()) this.fs.rmdirSync(this.host(path)); else this.fs.unlinkSync(this.host(path));
  }

  rename(path: string, to: string, expectedVersion: string | null) {
    if (this.lstat(to) !== null) throw new MountConflictError(`${to}: already exists`);
    const st = this.lstat(path);
    if (st === null || !st.isDirectory()) this.check(path, expectedVersion);
    this.fs.renameSync(this.host(path), this.host(to));
  }

  mkdir(path: string) { this.fs.mkdirSync(this.host(path)); }
}

/** Files kept in memory: for tests, and as an example of a source. `files` maps paths ("notes/a.md"; a trailing "/" for an
 * empty directory) to text or bytes. set() and remove() change it from outside; each change takes the next version ("1",
 * "2", ...). hook(op, path), called before every operation (it may return a Promise), can sleep or throw to simulate latency,
 * failures and conflicts. calls lists every [op, path] asked. */
export class MemorySource implements MountSource {
  readonly type = 'memory';
  readonly capabilities = CAPABILITIES;
  hook: ((op: string, path: string) => unknown) | null = null;
  readonly calls: [op: string, path: string][] = [];
  private next = 0;
  private readonly items = new Map<string, [content: Uint8Array | null, version: string]>([['/', [null, '0']]]);

  constructor(files: Record<string, string | Uint8Array | null> = {}, readonly id = 'memory', readonly modified: number = Date.UTC(2026, 0, 1)) {
    for (const p of Object.keys(files).sort(byCodePoint)) {
      if (p.endsWith('/')) this.mkdirs(VirtualFileSystem.normalize('/', p));
      else this.set(p, files[p] ?? '');
    }
  }

  private version() { return String(++this.next); }
  private mkdirs(path: string) {
    if (this.items.has(path)) return;
    this.mkdirs(parentOf(path));
    this.items.set(path, [null, this.version()]);
  }
  private isDir(path: string) { return this.items.has(path) && this.items.get(path)![0] === null; }

  set(path: string, content: string | Uint8Array) {
    path = VirtualFileSystem.normalize('/', path);
    this.mkdirs(parentOf(path));
    this.items.set(path, [typeof content === 'string' ? new TextEncoder().encode(content) : content.slice(), this.version()]);
  }

  remove(path: string) {
    path = VirtualFileSystem.normalize('/', path);
    for (const p of [...this.items.keys()]) if (p === path || p.startsWith(path + '/')) this.items.delete(p);
  }

  /** Every path below the root and its content (null: a directory), sorted. */
  tree(): Record<string, Uint8Array | null> {
    const out: Record<string, Uint8Array | null> = {};
    for (const p of [...this.items.keys()].sort(byCodePoint)) if (p !== '/') out[p] = this.items.get(p)![0];
    return out;
  }

  private async enter(op: string, path: string) {
    this.calls.push([op, path]);
    if (this.hook) await this.hook(op, path);
  }

  private entry(path: string): MountEntry | null {
    const item = this.items.get(path);
    if (!item) return null;
    return { name: nameOf(path), isDir: item[0] === null, size: item[0]?.length ?? 0, modified: this.modified, version: item[1] };
  }

  private check(path: string, expected: string | null) {
    const now = this.items.get(path)?.[1] ?? null;
    if (now !== expected) throw new MountConflictError(`${path}: version ${now ?? 'None'}, expected ${expected ?? 'None'}`);
  }

  async list(path: string): Promise<MountEntry[]> {
    await this.enter('list', path);
    if (!this.isDir(path)) throw notFound(path);
    const prefix = path.replace(/\/+$/, '') + '/';
    return [...this.items.keys()].filter(p => p !== '/' && p.startsWith(prefix) && !p.slice(prefix.length).includes('/')).map(p => this.entry(p)!);
  }

  async stat(path: string): Promise<MountEntry | null> {
    await this.enter('stat', path);
    return this.entry(path);
  }

  async read(path: string, _maxBytes: number): Promise<Uint8Array> {
    await this.enter('read', path);
    const data = this.items.get(path)?.[0];
    if (!data) throw notFound(path);
    return data;
  }

  async write(path: string, data: Uint8Array, expectedVersion: string | null): Promise<string> {
    await this.enter('write', path);
    this.check(path, expectedVersion);
    if (!this.isDir(parentOf(path))) throw notFound(parentOf(path));
    const version = this.version();
    this.items.set(path, [data.slice(), version]);
    return version;
  }

  async delete(path: string, expectedVersion: string | null): Promise<void> {
    await this.enter('delete', path);
    if (this.isDir(path)) {
      if ([...this.items.keys()].some(p => p.startsWith(path + '/'))) throw new Error(`${path}: Directory not empty`);
    } else this.check(path, expectedVersion);
    this.items.delete(path);
  }

  async rename(path: string, to: string, expectedVersion: string | null): Promise<void> {
    await this.enter('rename', path);
    if (this.items.has(to)) throw new MountConflictError(`${to}: already exists`);
    if (!this.isDir(path)) this.check(path, expectedVersion);
    if (!this.isDir(parentOf(to))) throw notFound(parentOf(to));
    for (const p of [...this.items.keys()]) {
      if (p !== path && !p.startsWith(path + '/')) continue;
      const item = this.items.get(p)!;
      this.items.delete(p);
      this.items.set(to + p.slice(path.length), item);
    }
  }

  async mkdir(path: string): Promise<void> {
    await this.enter('mkdir', path);
    if (this.items.has(path)) throw new MountConflictError(`${path}: already exists`);
    if (!this.isDir(parentOf(path))) throw notFound(parentOf(path));
    this.items.set(path, [null, this.version()]);
  }
}

// ---------- changes ----------

/** One operation that makes the source match the mount: kind write, delete, rename or mkdir; path (and `to` for a rename)
 * within the source; version: what the entry was when read (null: it must not exist); content for a write. */
export interface MountChange {
  readonly kind: 'write' | 'delete' | 'rename' | 'mkdir';
  readonly path: string;
  readonly to: string | null;
  readonly version: string | null;
  readonly content: Uint8Array | null;
}

/** What applyChanges did: the changes applied, the one it stopped at (null if none) with the error, and whether that was a
 * conflict (the source changed since it was read). */
export interface MountApplyResult {
  applied: MountChange[];
  failed: MountChange | null;
  error: string | null;
  conflict: boolean;
}

/** A mount as `shell.mounts` lists it. error: why it is unavailable (null when it works); warnings: what its last listing left out. */
export interface MountInfo {
  path: string;
  mode: MountMode;
  type: string;
  id: string;
  error: string | null;
  warnings: string[];
}

/** One question a recording kept and its answer: list (entries), stat (entry; null: missing), read (body), write (version),
 * delete, rename, mkdir; error and conflict when the source refused. */
export interface MountExchange {
  mount: string;
  op: MountOp;
  path: string;
  to: string | null;
  entries: MountEntry[] | null;
  entry: MountEntry | null;
  body: Uint8Array | null;
  version: string | null;
  error: string | null;
  conflict: boolean;
}

/** @internal */
export type MountOp = 'list' | 'stat' | 'read' | 'write' | 'delete' | 'rename' | 'mkdir';
/** @internal */
export const MOUNT_OPS: readonly MountOp[] = ['list', 'stat', 'read', 'write', 'delete', 'rename', 'mkdir'];

/** @internal A mount as a snapshot keeps it. */
export interface MountState { path: string; mode: MountMode; type: string; id: string; changes: MountChange[] }

/** @internal */
export function mountChange(kind: MountChange['kind'], path: string, o: { to?: string | null; version?: string | null; content?: Uint8Array | null } = {}): MountChange {
  return { kind, path, to: o.to ?? null, version: o.version ?? null, content: o.content ?? null };
}

const sameBytes = (x: Uint8Array | null, y: Uint8Array | null) => x === y || (!!x && !!y && x.length === y.length && x.every((b, i) => b === y[i]));
const sameChange = (a: MountChange, b: MountChange) =>
  a.kind === b.kind && a.path === b.path && a.to === b.to && a.version === b.version && sameBytes(a.content, b.content);
const sameChanges = (a: MountChange[], b: MountChange[]) => a.length === b.length && a.every((c, i) => sameChange(c, b[i]));

// ---------- mounts ----------

/** A listed entry that passed the checks: every field filled in. */
type Entry = { name: string; isDir: boolean; size: number; modified: number | null; version: string };

/** What the mount read from the source at one path: the entry, the node it became, and its content's hash once fetched. */
interface Base {
  entry: Entry;
  node: Node | null;
  digest: string | null;
  clock: number | null; // the node's change clock right after it was fetched: unchanged since means clean, can be evicted
}

class Mount {
  readonly type: string;
  readonly id: string;
  base = new Map<string, Base>();
  expected = new Map<string, string | null>(); // versions a replayed change was made on, which win over base's
  absent = new Set<string>(); // paths a replayed write expects not to exist yet
  pending: MountChange[] | null = null; // the changes while not built (after a restore, or unavailable)
  error: string | null = null;
  warnings: string[] = [];
  misses = new Map<FileNode, FileNode>(); // files read before they were fetched: fetched after the command
  failed = new Map<Node, string>(); // why a file could not be fetched
  fetched = new Map<Node, Base>(); // in fetch order, for eviction
  stuck: MountChange[] | null = null; // live mode: the changes left when applying last failed, not retried until they change
  readonly miss = (node: FileNode, path: string) => {
    let reason = this.failed.get(node);
    if (reason === undefined) { this.misses.set(node, node); reason = msg('notFetched'); }
    throw new ShellError(`${path}: ${reason}`);
  };

  constructor(readonly mounts: Mounts, readonly path: string, readonly source: MountSource | null, readonly mode: MountMode, type?: string, id?: string) {
    this.type = type ?? String(source?.type ?? '');
    this.id = id ?? String(source?.id ?? '');
  }

  get fs() { return this.mounts.shell.fs; }

  info(): MountInfo { return { path: this.path, mode: this.mode, type: this.type, id: this.id, error: this.error, warnings: [...this.warnings] }; }

  machine(p: string) { return p === '/' ? this.path : this.path + p; }

  // ----- talking to the source -----

  /** Calls the source, bounded by the call's time limit, and keeps the exchange while recording. */
  async call(op: MountOp, path: string, args: unknown[] = [], to: string | null = null): Promise<any> {
    const shell = this.mounts.shell;
    const x = { mount: this.path, op, path, to, entries: null, entry: null, body: null, version: null, error: null, conflict: false };
    let r: any;
    try {
      const fn = (this.source as any)?.[op];
      if (typeof fn !== 'function') throw new Error(`the source has no ${op}`);
      r = op === 'rename' ? fn.call(this.source, path, to, ...args) : fn.call(this.source, path, ...args);
      if (r && typeof r.then === 'function') {
        const left = shell.remainingMs();
        if (left === Infinity) r = await r;
        else {
          let timer: ReturnType<typeof setTimeout> | undefined;
          const late = new Promise((_, reject) => { timer = setTimeout(() => reject(TIMED_OUT), Math.max(left, 0) + 1); }); // +1: past the deadline, which checkTime compares strictly
          try { r = await Promise.race([r, late]); } finally { clearTimeout(timer); }
        }
      }
    } catch (e) {
      if (e === TIMED_OUT) {
        this.mounts.log({ ...x, error: msg('timedOut') });
        throw new ShellError(msg('timedOut'));
      }
      if (isControl(e)) throw e;
      if (e instanceof MountConflictError) this.mounts.log({ ...x, error: e.message || 'conflict', conflict: true });
      else this.mounts.log({ ...x, error: why(e) });
      throw e;
    }
    if (op === 'list') { r = [...(r ?? [])]; this.mounts.log({ ...x, entries: r }); }
    else if (op === 'stat') this.mounts.log({ ...x, entry: r ?? null });
    else if (op === 'read') this.mounts.log({ ...x, body: r instanceof Uint8Array ? r.slice() : null });
    else this.mounts.log({ ...x, version: op === 'write' ? r ?? null : null });
    return r;
  }

  /** Every entry, parents first, bounded by the entry limit and MAX_DEPTH; hostile names are skipped. */
  async listTree(): Promise<Map<string, Entry>> {
    const limit = this.mounts.shell.options.maxExtractEntries;
    const out = new Map<string, Entry>();
    this.warnings = [];
    let skipped = 0;
    let queue: [string, number][] = [['/', 0]];
    while (queue.length) {
      const [d, depth] = queue.shift()!;
      const names = new Set<string>(), good: Entry[] = [];
      for (const raw of await this.call('list', d)) {
        const e = valid(raw);
        if (!e || names.has(e.name)) { skipped++; continue; }
        names.add(e.name);
        good.push(e);
      }
      for (const e of good.sort((a, b) => byCodePoint(a.name, b.name))) {
        if (out.size >= limit) {
          this.warnings.push(msg('truncated', { dir: this.path, limit: String(limit) }));
          queue = [];
          break;
        }
        const p = join(d, e.name);
        out.set(p, e);
        if (e.isDir && depth + 1 < MAX_DEPTH) queue.push([p, depth + 1]);
      }
      this.mounts.shell.checkTime();
    }
    if (skipped) this.warnings.push(msg('skipped', { dir: this.path, count: String(skipped) }));
    return out;
  }

  // ----- the files in the machine -----

  /** Replaces the mount point's tree with placeholders for `entries` (source path -> entry, parents first). */
  build(entries: Map<string, Entry>) {
    const fs = this.fs, ro = this.mode === 'read-only';
    const owner = ro ? 'root' : this.mounts.shell.options.user;
    const [fmode, dmode] = ro ? [0o444, 0o555] : [0o644, 0o755];
    if (fs.exists(this.path, false)) fs.delete(this.path, true);
    fs.mkdir(this.path, true);
    const root = fs.nodeAt(this.path)!;
    Object.assign(root, { owner, mode: dmode });
    this.base = new Map([['/', { entry: { name: '', isDir: true, size: 0, modified: null, version: '' }, node: root, digest: null, clock: null }]]);
    this.fetched = new Map(); this.misses = new Map(); this.failed = new Map();
    for (const [p, e] of entries) {
      if (p === '/') continue;
      const parent = this.base.get(parentOf(p));
      if (!parent || !parent.entry.isDir) continue;
      const modified = e.modified ?? fs.clock();
      const node: Node = e.isDir ? Object.assign(new Dir(), { modified, owner, mode: dmode })
        : Object.assign(new FileNode(), { modified, owner, mode: fmode, reportedSize: e.size, loader: this.miss });
      (parent.node as Dir).children.set(nameOf(p), node);
      this.base.set(p, { entry: e, node, digest: null, clock: null });
    }
    this.pending = null; this.error = null;
  }

  /** An empty mount point the agent can't open; the changes are kept for a later mount. */
  unavailable(reason: string | null) {
    const fs = this.fs;
    if (fs.exists(this.path, false)) fs.delete(this.path, true);
    fs.mkdir(this.path, true);
    fs.chmod(this.path, 0);
    this.base = new Map();
    this.error = reason;
  }

  /** Makes `changes` again on a freshly built tree, remembering the versions they were made on. */
  replay(changes: MountChange[]) {
    const fs = this.fs;
    this.expected = new Map(); this.absent = new Set();
    for (const c of changes) {
      const path = this.machine(c.path);
      try {
        if (c.kind === 'write') { fs.mkdir(parentOf(path), true); store(fs, path, c.content ?? new Uint8Array(0)); }
        else if (c.kind === 'mkdir') fs.mkdir(path, true);
        else if (c.kind === 'rename') fs.move(path, this.machine(c.to!));
        else if (c.kind === 'delete') fs.delete(path, true);
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        this.warnings.push(`${path}: ${e.message}`);
      }
      if (c.kind === 'write' && c.version === null) this.absent.add(c.path);
      else if (c.kind !== 'mkdir') this.expected.set(c.path, c.version);
    }
  }

  /** Fetches placeholders, making room by evicting clean fetched files; failures are kept for miss(). */
  async fetch(nodes: FileNode[]) {
    const fs = this.fs;
    const where = new Map<Node, [string, Base]>();
    for (const [p, b] of this.base) if (b.node) where.set(b.node, [p, b]);
    for (const node of nodes) {
      if (!node.loader || !where.has(node)) continue;
      const [p, b] = where.get(node)!;
      let room = fs.capacity - fs.usedBytes;
      if (b.entry.size > room) room += this.evict(b.entry.size - room);
      let data: Uint8Array;
      try {
        if (b.entry.size > room) throw new ShellError(msg('tooLarge'));
        const r = await this.call('read', p, [room]);
        if (!(r instanceof Uint8Array)) throw new ShellError('the source returned something other than bytes');
        data = r.slice();
        if (data.length > room) throw new ShellError(msg('tooLarge'));
      } catch (e) {
        if (isControl(e)) throw e;
        this.failed.set(node, why(e));
        continue;
      }
      if (!node.loader) continue; // written while we waited
      fill(fs, node, data);
      b.digest = sha1(data); b.clock = node.changed;
      this.failed.delete(node);
      this.fetched.set(node, b);
    }
  }

  /** Turns the oldest unchanged fetched files back into placeholders; returns the bytes freed. */
  evict(need: number): number {
    let freed = 0;
    for (const [key, b] of [...this.fetched]) {
      if (freed >= need) break;
      this.fetched.delete(key);
      const n = b.node as FileNode;
      if (n.loader || n.changed !== b.clock || n.nlink !== 1) continue;
      freed += n.size;
      this.fs.usedBytes -= n.size;
      n.content = ''; n.data = null; n.size = 0;
      n.reportedSize = b.entry.size; n.loader = this.miss;
      b.digest = b.clock = null;
    }
    return freed;
  }

  /** The placeholders at or below a machine path (no symbolic link is followed). */
  stubsUnder(path: string): FileNode[] {
    const out: FileNode[] = [], stack: (Node | null)[] = [this.fs.nodeAt(path, false)];
    while (stack.length) {
      const n = stack.pop();
      if (n instanceof FileNode && n.loader) out.push(n);
      else if (n instanceof Dir) for (const k of [...n.children.keys()].sort(byCodePoint).reverse()) stack.push(n.children.get(k)!);
    }
    return out;
  }

  // ----- the change set -----

  /** The version a base path was read with (or that a replayed change was made on). */
  oldVersion(p: string): string | null {
    if (this.expected.has(p)) return this.expected.get(p)!;
    return this.base.get(p)?.entry.version ?? null;
  }

  changes(): MountChange[] {
    if (this.pending !== null) return [...this.pending];
    const root = this.fs.nodeAt(this.path, false);
    if (!(root instanceof Dir) || this.base.size === 0) return [];
    const inTree = nodesOf(root);
    const at = new Map<Node, string>();
    for (const [p, b] of this.base) if (p !== '/' && b.node) at.set(b.node, p);
    const gone = [...this.base].filter(([p, b]) => p !== '/' && !inTree.has(b.node!)).map(([p]) => p).sort(byCodePoint).reverse();
    const dropped = new Set<string>(), visited = new Set<Node>();
    const first: MountChange[] = [], renames: MountChange[] = [], mkdirs: MountChange[] = [], writes: MountChange[] = [];
    const writeVersion = (p: string) => this.absent.has(p) ? null : this.oldVersion(p);
    // p is gone and something else stands there now: delete it, and what was below it, first
    const drop = (p: string) => {
      for (const q of gone)
        if ((q === p || q.startsWith(p + '/')) && !dropped.has(q)) { dropped.add(q); first.push(mountChange('delete', q, { version: this.oldVersion(q) })); }
    };
    const walk = (d: Dir, path: string, was: string | null) => {
      for (const name of [...d.children.keys()].sort(byCodePoint)) {
        const n = d.children.get(name)!;
        if (!(n instanceof Dir) && !(n instanceof FileNode)) continue; // symbolic links are not part of a change set
        const p = join(path, name);
        let bp = at.get(n) ?? null;
        if (visited.has(n)) bp = null; // a second hard link
        visited.add(n);
        const old = this.base.get(p);
        const replaced = old !== undefined && !inTree.has(old.node!);
        if (bp !== null) {
          if (bp !== (was !== null ? join(was, name) : null)) {
            if (replaced) drop(p);
            renames.push(mountChange('rename', bp, { to: p, version: this.oldVersion(bp) }));
          }
          const b = this.base.get(bp)!;
          if (n instanceof Dir) walk(n, p, bp);
          else if (!n.loader && n.changed !== b.clock && sha1(n.raw) !== b.digest)
            writes.push(mountChange('write', p, { version: writeVersion(bp), content: n.raw }));
          continue;
        }
        if (replaced && (n instanceof FileNode) === !old!.entry.isDir) {
          dropped.add(p); // a file replaced by a file is a write; a directory made again is the same one
          if (n instanceof Dir) walk(n, p, p);
          else if (!n.loader) writes.push(mountChange('write', p, { version: writeVersion(p), content: n.raw }));
          continue;
        }
        if (replaced) drop(p);
        if (n instanceof Dir) {
          mkdirs.push(mountChange('mkdir', p));
          walk(n, p, null);
        } else if (!n.loader) { // shortcut: a placeholder moved in from another mount is left out
          const version = this.base.has(p) || this.absent.has(p) ? null : this.expected.get(p) ?? null;
          writes.push(mountChange('write', p, { version, content: n.raw }));
        }
      }
    };
    walk(root, '/', '/');
    const deletes = gone.filter(p => !dropped.has(p)).map(p => mountChange('delete', p, { version: this.oldVersion(p) }));
    return [...first, ...renames, ...mkdirs, ...writes, ...deletes];
  }

  async apply(changes?: MountChange[] | null): Promise<MountApplyResult> {
    const current = this.changes();
    const todo = changes == null ? current : changes.filter(c => current.some(x => sameChange(c, x)));
    if (this.pending !== null || this.error !== null)
      return { applied: [], failed: todo[0] ?? null, error: this.error ?? 'the mount is not listed yet', conflict: false };
    const caps = new Set<string>(this.source?.capabilities ?? []);
    const applied: MountChange[] = [];
    for (const c of todo) {
      let version: string | null = null;
      try {
        if (this.mode === 'read-only') throw new ShellError('the mount is read-only');
        if (!caps.has(c.kind)) throw new ShellError(`the source cannot ${c.kind}`);
        const now: MountEntry | null = await this.call('stat', c.path);
        if (c.kind === 'mkdir' || (c.kind !== 'write' && now && now.isDir)) {
          if (!!now !== (c.kind !== 'mkdir')) // directories: only whether they exist
            throw new MountConflictError(`${c.path}: ${now ? 'already exists' : 'missing'}`);
        } else if ((now ? now.version ?? '' : null) !== c.version)
          throw new MountConflictError(`${c.path}: version ${now ? now.version ?? '' : 'None'}, expected ${c.version ?? 'None'}`);
        if (c.kind === 'rename' && await this.call('stat', c.to!)) throw new MountConflictError(`${c.to}: already exists`);
        if (c.kind === 'write') version = await this.call('write', c.path, [c.content ?? new Uint8Array(0), c.version]);
        else if (c.kind === 'delete') await this.call('delete', c.path, [c.version]);
        else if (c.kind === 'rename') await this.call('rename', c.path, [c.version], c.to);
        else await this.call('mkdir', c.path);
      } catch (e) {
        if (isControl(e)) throw e;
        return { applied, failed: c, error: e instanceof MountConflictError ? e.message || 'conflict' : why(e), conflict: e instanceof MountConflictError };
      }
      this.applied(c, version);
      applied.push(c);
    }
    return { applied, failed: null, error: null, conflict: false };
  }

  /** What a change applied to the source becomes the mount's new starting point. */
  applied(c: MountChange, version: string | null) {
    for (const p of [c.path, c.to]) if (p !== null) { this.expected.delete(p); this.absent.delete(p); }
    if (c.kind === 'delete') { this.base.delete(c.path); return; }
    if (c.kind === 'rename') {
      for (const p of [...this.base.keys()].filter(p => p === c.path || p.startsWith(c.path + '/')).sort(byCodePoint)) {
        const b = this.base.get(p)!;
        this.base.delete(p);
        this.base.set(c.to + p.slice(c.path.length), b);
      }
      return;
    }
    const node = this.fs.nodeAt(this.machine(c.path), false);
    const now = this.fs.clock();
    if (c.kind === 'mkdir') {
      this.base.set(c.path, { entry: { name: nameOf(c.path), isDir: true, size: 0, modified: now, version: '' }, node, digest: null, clock: null });
      return;
    }
    const data = c.content ?? new Uint8Array(0);
    const b: Base = { entry: { name: nameOf(c.path), isDir: false, size: data.length, modified: now, version: String(version ?? '') }, node, digest: sha1(data), clock: null };
    this.base.set(c.path, b);
    if (node instanceof FileNode) b.clock = sha1(node.raw) === b.digest ? node.changed : null;
  }
}

const TIMED_OUT = Symbol('timed out');

/** A shell's mounts (Shell.mount and friends call these), the sources it knows by type and id, and, while recording, the
 * source exchanges of the current call. @internal */
export class Mounts {
  items = new Map<string, Mount>();
  sources = new Map<string, MountSource>(); // every source mounted, for restore
  replay: MountExchange[] | null = null; // a replay's recorded exchanges: answered instead of any source
  recording = false;
  private exchanges: MountExchange[] = [];

  constructor(readonly shell: Shell) {}

  log(x: MountExchange) { if (this.recording) this.exchanges.push(x); }
  takeLog(): MountExchange[] { const log = this.exchanges; this.exchanges = []; return log; }

  /** The mount holding a machine path. */
  find(path: string): Mount | null {
    for (const m of this.items.values()) if (path === m.path || path.startsWith(m.path + '/')) return m;
    return null;
  }

  get(path: string): Mount {
    const m = this.items.get(VirtualFileSystem.normalize('/', path));
    if (!m) throw new Error(`${path}: not a mount point`);
    return m;
  }

  sourceFor(type: string, id: string, path: string): MountSource | null {
    if (this.replay !== null) return new ReplaySource(this.replay, path, type, id);
    return this.sources.get(type + '\0' + id) ?? null;
  }

  // ----- the host's API -----

  async mount(path: string, source: MountSource, mode: MountMode = 'read-only') {
    if (!MOUNT_MODES.includes(mode)) throw new Error(`${mode}: the mode is one of ${MOUNT_MODES.join(', ')}`);
    if (!path.startsWith('/')) throw new Error(`${path}: a mount point is an absolute path other than /`);
    path = VirtualFileSystem.normalize('/', path);
    if (path === '/') throw new Error(`${path}: a mount point is an absolute path other than /`);
    if (mode !== 'read-only' && !new Set<string>(source.capabilities ?? []).has('write'))
      throw new Error(`${path}: the source can't write, so it can only be mounted read-only`);
    const old = this.items.get(path);
    let changes: MountChange[] = [];
    if (old) {
      if (old.error === null || old.type !== String(source.type ?? '') || old.id !== String(source.id ?? ''))
        throw new Error(`${path}: already a mount point; unmount it first`);
      changes = old.changes();
    } else {
      for (const p of this.items.keys())
        if (path.startsWith(p + '/') || p.startsWith(path + '/')) throw new Error(`${path}: inside another mount, or holds one`);
      const fs = this.shell.fs;
      if (fs.exists(path, false) && !(fs.isDirectory(path, false) && fs.list(path).length === 0))
        throw new Error(`${path}: exists and is not an empty directory`);
    }
    const m = new Mount(this, path, source, mode);
    const entries = await m.listTree();
    this.items.set(path, m);
    this.sources.set(m.type + '\0' + m.id, source);
    m.build(entries);
    m.replay(changes);
  }

  unmount(path: string) {
    const m = this.get(path), fs = this.shell.fs;
    this.items.delete(m.path);
    if (fs.exists(m.path, false)) fs.delete(m.path, true);
    fs.mkdir(m.path);
  }

  async refresh(path: string) {
    const m = this.get(path);
    if (!m.source) throw new ShellError(m.error ?? 'unavailable');
    const changes = m.changes();
    const entries = await m.listTree();
    m.build(entries);
    m.replay(changes);
  }

  discard(path: string) {
    const m = this.get(path);
    if (m.pending !== null) { m.pending = []; return; }
    m.build(new Map([...m.base].map(([p, b]) => [p, b.entry])));
    m.expected = new Map(); m.absent = new Set();
  }

  // ----- around commands -----

  /** At the start of a call: lists the mounts a restore brought back. */
  async prepare() {
    for (const m of [...this.items.values()]) {
      if (m.pending === null || !m.source) continue;
      const changes = m.pending;
      let entries;
      try { entries = await m.listTree(); } catch (e) { if (isControl(e)) throw e; m.error = why(e); continue; }
      m.build(entries);
      m.replay(changes);
    }
  }

  /** Fetches what a command is about to read: the paths it names (a directory: everything below), or the working directory. */
  async before(name: string, argv: string[], redirect?: string) {
    if (NO_CONTENT.has(name) && redirect === undefined) return;
    const shell = this.shell, fs = shell.agentFs;
    const paths: string[] = [];
    for (const w of redirect !== undefined ? [redirect] : argv)
      for (const part of w.startsWith('-') && w.includes('=') ? [w, w.slice(w.indexOf('=') + 1)] : [w])
        if (part) { const p = shell.resolvePath(part); paths.push(fs.realPath(p) ?? p); }
    let found: [Mount, string][] = [];
    for (const p of paths) { const m = this.find(p); if (m && fs.exists(p, false)) found.push([m, p]); }
    if (!found.length && redirect === undefined) {
      const cwd = fs.realPath(shell.cwdPath) ?? shell.cwdPath, m = this.find(cwd);
      found = m ? [[m, cwd]] : [];
    }
    for (const [m, p] of found)
      if (m.error === null && m.pending === null && fs.exists(p, false)) await m.fetch(m.stubsUnder(p));
  }

  /** After a command: fetches what it missed, and in live mode applies its changes. */
  async after(err: Writer) {
    for (const m of [...this.items.values()]) {
      if (m.misses.size) {
        const nodes = [...m.misses.values()];
        m.misses = new Map();
        await m.fetch(nodes);
      }
      if (m.mode === 'live' && m.error === null && m.pending === null) {
        const changes = m.changes();
        if (!changes.length || (m.stuck && sameChanges(changes, m.stuck))) continue; // nothing new since the last attempt failed
        const r = await m.apply(null);
        m.stuck = r.failed ? m.changes() : null;
        if (r.failed) {
          const where = m.machine(r.failed.path);
          err.write('mount: ' + (r.conflict ? msg('conflict', { path: where }) : msg('failed', { path: where, reason: r.error ?? '' })) + '\n');
        }
      }
    }
  }

  // ----- snapshots and recordings -----

  /** The filesystem's entries without what is inside a mount (a snapshot keeps the mount's definition instead). */
  outside<E extends { path: string; hardLink: string | null }>(entries: Iterable<E>): E[] {
    if (!this.items.size) return [...entries];
    const out: E[] = [];
    for (const e of entries)
      if (!this.find(e.path)) out.push(e.hardLink !== null && this.find(e.hardLink) ? { ...e, hardLink: null } : e); // it carries the content
    return out;
  }

  capture(): MountState[] {
    return [...this.items.values()].map(m => ({ path: m.path, mode: m.mode, type: m.type, id: m.id, changes: m.changes() }));
  }

  /** After the files were replaced: the snapshot's mounts, listed at the start of the next call. */
  restore(saved: readonly MountState[]) {
    this.items = new Map();
    for (const d of saved) {
      const source = this.sourceFor(d.type, d.id, d.path);
      const m = new Mount(this, d.path, source, d.mode, d.type, d.id);
      this.items.set(m.path, m);
      m.unavailable(source ? null : msg('unavailable', { source: `${m.type}:${m.id}` }));
      m.pending = [...d.changes];
    }
  }

  /** What the mounts hold when a recording starts, as the exchanges a replay's first listing and reads answer from. */
  startExchanges(): MountExchange[] {
    const out: MountExchange[] = [];
    const x = { to: null, entries: null, entry: null, body: null, version: null, error: null, conflict: false };
    for (const m of this.items.values()) {
      if (m.pending !== null || m.error !== null) continue;
      const kids = new Map<string, MountEntry[]>();
      for (const [p, b] of m.base) if (b.entry.isDir) kids.set(p, []);
      for (const [p, b] of m.base) if (p !== '/' && kids.has(parentOf(p))) kids.get(parentOf(p))!.push(b.entry);
      for (const [p, b] of m.base) {
        if (b.entry.isDir) out.push({ ...x, mount: m.path, op: 'list', path: p, entries: kids.get(p)! });
        else if (b.digest !== null && b.node instanceof FileNode && !b.node.loader && sha1(b.node.raw) === b.digest)
          out.push({ ...x, mount: m.path, op: 'read', path: p, body: b.node.raw });
      }
    }
    return out;
  }
}

/** Answers a replay's questions from the recorded exchanges of one mount, never the real source. */
class ReplaySource implements MountSource {
  readonly capabilities = CAPABILITIES;
  private readonly answers = new Map<string, MountExchange[]>();

  constructor(exchanges: readonly MountExchange[], mount: string, readonly type: string, readonly id: string) {
    for (const x of exchanges) {
      if (x.mount !== mount) continue;
      const k = key(x.op, x.path, x.to);
      const q = this.answers.get(k);
      if (q) q.push(x); else this.answers.set(k, [x]);
    }
  }

  private answer(op: MountOp, path: string, to: string | null = null): MountExchange {
    const q = this.answers.get(key(op, path, to));
    if (!q?.length) throw new ShellError(msg('replay', { op, path }));
    const x = q.length > 1 ? q.shift()! : q[0]; // the last answer repeats
    if (x.conflict) throw new MountConflictError(x.error ?? '');
    if (x.error !== null) throw new ShellError(x.error);
    return x;
  }

  list(path: string) { return [...this.answer('list', path).entries ?? []]; }
  stat(path: string) { return this.answer('stat', path).entry; }
  read(path: string, _maxBytes: number) { return this.answer('read', path).body ?? new Uint8Array(0); }
  write(path: string) { return this.answer('write', path).version ?? ''; }
  delete(path: string) { this.answer('delete', path); }
  rename(path: string, to: string) { this.answer('rename', path, to); }
  mkdir(path: string) { this.answer('mkdir', path); }
}

// ---------- helpers ----------

const key = (op: string, path: string, to: string | null) => `${op}\0${path}\0${to ?? ''}`;

/** Python's str order (by code point): differs from `<` only for characters above U+FFFF against U+E000..U+FFFF. */
export function byCodePoint(a: string, b: string): number {
  for (let i = 0, j = 0; i < a.length && j < b.length;) {
    const x = a.codePointAt(i)!, y = b.codePointAt(j)!;
    if (x !== y) return x < y ? -1 : 1;
    i += x > 0xffff ? 2 : 1; j += y > 0xffff ? 2 : 1;
  }
  return a.length === b.length ? 0 : a.length < b.length ? -1 : 1;
}

/** An entry a source listed that can be a file: a plain name, sane size and version; null otherwise. */
function valid(e: unknown): Entry | null {
  if (!e || typeof e !== 'object') return null;
  const { name, isDir, size = 0, modified = null, version = '' } = e as MountEntry;
  if (typeof name !== 'string' || typeof version !== 'string') return null;
  if (name === '' || name === '.' || name === '..' || name.includes('/') || name.includes('\0') || new TextEncoder().encode(name).length > MAX_NAME_BYTES) return null;
  if (!Number.isSafeInteger(size) || size < 0 || version.length > MAX_VERSION) return null;
  const when = modified instanceof Date ? modified.getTime() : modified;
  if (when !== null && !(typeof when === 'number' && Number.isFinite(when))) return null;
  return { name, isDir: !!isDir, size, modified: when, version };
}

function nodesOf(root: Dir): Set<Node> {
  const out = new Set<Node>(), stack: Node[] = [root];
  while (stack.length) {
    const n = stack.pop()!;
    out.add(n);
    if (n instanceof Dir) stack.push(...n.children.values());
  }
  return out;
}

const strict = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });
const textOf = (data: Uint8Array): string | null => { try { return strict.decode(data); } catch { return null; } };

/** Puts fetched content into a placeholder without marking it changed by the agent. */
function fill(fs: VirtualFileSystem, node: FileNode, data: Uint8Array) {
  const text = textOf(data);
  if (text !== null) { node.content = text; node.data = null; } else { node.content = ''; node.data = data; }
  node.size = data.length;
  node.loader = null; node.reportedSize = null;
  fs.usedBytes += data.length;
}

function store(fs: VirtualFileSystem, path: string, data: Uint8Array) {
  const text = textOf(data);
  if (text !== null) fs.writeText(path, text); else fs.writeBytes(path, data);
}
