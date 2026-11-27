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
/** @internal How much one listing pass reads (entries, seconds) before it leaves the directories still to go as "not listed
 * yet"; an object so tests can lower it. */
export const LIST_LIMITS = { entries: 2000, seconds: 5 };
const REVEAL_BUDGET = 2000; // what naming a path lists (fixed, as Python's default argument is)
const FETCH_AT_ONCE = 4; // files fetched at the same time
// commands that never read a file's content: nothing is fetched before them (they may still list a directory)
const NO_CONTENT = new Set(['ls', 'find', 'stat', 'du', 'df', 'tree', 'cd', 'pwd', 'test', '[', 'mkdir', 'rmdir', 'rm', 'mv', 'touch',
  'mount', 'chmod', 'chown', 'chgrp', 'ln', 'realpath', 'readlink', 'basename', 'dirname', 'echo', 'printf',
  'true', 'false', 'date', 'sleep', 'seq']);
// commands that walk a directory they are given: everything below it is listed first
const DEEP = new Set(['find', 'du', 'tree', 'grep', 'egrep', 'fgrep', 'rg', 'cp', 'tar', 'zip', 'rm', 'diff', 'chmod', 'chown', 'chgrp',
  'to-markdown', 'wc', 'cat']);
const GREPS = new Set(['grep', 'egrep', 'fgrep', 'rg']);
const WINDOWS_RESERVED = new Set(['CON', 'PRN', 'AUX', 'NUL', ...[1, 2, 3, 4, 5, 6, 7, 8, 9].flatMap(i => [`COM${i}`, `LPT${i}`])]);

const msg = (key: string, values: Record<string, string> = {}) => specMessage('mount', key, values);
const join = (d: string, name: string) => d === '/' ? '/' + name : d + '/' + name;
const parentOf = (p: string) => VirtualFileSystem.getParent(p);
const nameOf = (p: string) => p.slice(p.lastIndexOf('/') + 1);
const why = (e: unknown) => e instanceof Error ? e.message || e.name : String(e);
/** @internal The call's time limit and other limits: never swallowed, they end the call. */
export const isControl = (e: unknown) => e instanceof DeadlineExceeded || e instanceof LimitExceeded || e instanceof ControlFlow;

// ---------- sources ----------

/** One entry a source lists: name (one path segment), isDir, size in bytes (null or absent: not known until read), modified
 * (ms since the epoch, UTC, or a Date; null: unknown) and version, an opaque string that changes when the entry does. */
export interface MountEntry {
  name: string;
  isDir: boolean;
  size?: number | null;
  modified?: number | Date | null;
  version?: string;
}

/** A source throws it when the expected version no longer matches what it holds. */
export class MountConflictError extends Error {}

/** A source throws it with a message the agent may see ("page.md: the page is locked"). Anything else a source throws is
 * shown to the agent as "the source failed"; its text goes to the host only. */
export class MountSourceError extends Error {}

type MaybePromise<T> = T | Promise<T>;
/** The last argument of every source method: `signal` is aborted at the call's time limit. */
export interface MountCallOptions { signal?: AbortSignal }

/** What a source implements (docs/mounts.md). Every method may return a Promise. Paths are canonical and absolute within the
 * source: "/" is its root. `type` and `id` are saved in snapshots, so they must not hold secrets; `capabilities` says which of
 * write, delete, rename and mkdir it supports (none: read-only). */
export interface MountSource {
  readonly type: string;
  readonly id: string;
  readonly capabilities: Iterable<MountCapability>;
  /** A directory's entries; more than maxEntries are not used, so a source that pages may stop there. */
  list(path: string, maxEntries: number, options?: MountCallOptions): MaybePromise<Iterable<MountEntry>>;
  stat(path: string, options?: MountCallOptions): MaybePromise<MountEntry | null>;
  read(path: string, maxBytes: number, options?: MountCallOptions): MaybePromise<Uint8Array>;
  /** Creates the file (expectedVersion null: it must not exist) or replaces it; returns its new version. */
  write?(path: string, data: Uint8Array, expectedVersion: string | null, options?: MountCallOptions): MaybePromise<string>;
  delete?(path: string, expectedVersion: string | null, options?: MountCallOptions): MaybePromise<void>;
  /** Returns the entry's new version (null or nothing: unchanged). */
  rename?(path: string, to: string, expectedVersion: string | null, options?: MountCallOptions): MaybePromise<string | null | void>;
  /** Returns the new directory's version (null or nothing: none). */
  mkdir?(path: string, options?: MountCallOptions): MaybePromise<string | null | void>;
}

const builtin = (id: string): any => (globalThis as { process?: { getBuiltinModule?: (id: string) => unknown } }).process?.getBuiltinModule?.(id);
const ERRNO_TEXT: Record<string, string> = {
  ENOENT: 'No such file or directory', EACCES: 'Permission denied', EPERM: 'Operation not permitted', EEXIST: 'File exists',
  ENOTDIR: 'Not a directory', EISDIR: 'Is a directory', ENOTEMPTY: 'Directory not empty', ELOOP: 'Too many levels of symbolic links',
  ENAMETOOLONG: 'File name too long', ENOSPC: 'No space left on device', EROFS: 'Read-only file system', EBUSY: 'Device or resource busy',
};
/** strerror's text for a Node error, never its message (which names the host path). */
function strerror(e: unknown): string {
  const err = e as { code?: string; errno?: number };
  if (err.code && ERRNO_TEXT[err.code]) return ERRNO_TEXT[err.code];
  try { if (typeof err.errno === 'number') return builtin('node:util').getSystemErrorMessage(err.errno); } catch { /* older Node */ }
  return err.code ?? 'failed';
}

/** A directory on the host (an Obsidian vault, a folder of notes); Node only. Read-only unless `writable`. Only regular files
 * and directories are listed: symbolic links, FIFOs and devices are left out, never followed or opened. Names with `\` (and on
 * Windows `:`, reserved device names and a trailing dot or space) are refused. Node has no openat, so each name is checked
 * for a link just before use: a link swapped in between could still be followed (docs/mounts.md). The version is the
 * modification time and size. */
export class LocalFolderSource implements MountSource {
  readonly type = 'local-folder';
  readonly id: string;
  readonly capabilities: readonly MountCapability[];
  private readonly root: string;
  private readonly fs: any;
  private readonly windows: boolean;

  constructor(root: string, options: { writable?: boolean; id?: string } = {}) {
    this.fs = builtin('node:fs');
    if (!this.fs) throw new Error('LocalFolderSource needs Node (process.getBuiltinModule)');
    let real: string;
    try { real = this.fs.realpathSync(root); } catch { throw new Error(`${root}: not a directory`); }
    if (!this.fs.statSync(real).isDirectory()) throw new Error(`${root}: not a directory`);
    this.root = real.replace(/[\\/]+$/, '') || real;
    this.windows = (globalThis as { process?: { platform?: string } }).process?.platform === 'win32';
    this.id = options.id ?? nameOf(this.root.replace(/\\/g, '/'));
    this.capabilities = options.writable ? CAPABILITIES : [];
  }

  private names(path: string): string[] {
    if (VirtualFileSystem.normalize('/', path) !== path || path.includes('\0')) throw new MountSourceError(`${path}: not a canonical path`);
    const names = path === '/' ? [] : path.split('/').slice(1);
    for (const n of names)
      if (n.includes('\\') || (this.windows && (n.includes(':') || n !== n.replace(/[ .]+$/, '') || WINDOWS_RESERVED.has(n.split('.')[0].toUpperCase()))))
        throw new MountSourceError(`${path}: not a name a file can have here`);
    return names;
  }

  /** The host directory `names` leads to, no link followed on the way (each checked just before use). */
  private dir(names: string[]): string {
    let cur = this.root;
    for (const n of names) {
      cur += '/' + n;
      const st = this.fs.lstatSync(cur, { throwIfNoEntry: false });
      if (!st || st.isSymbolicLink()) throw Object.assign(new Error(''), { code: 'ENOENT' });
    }
    if (cur !== this.root && !cur.startsWith(this.root + '/')) throw Object.assign(new Error(''), { code: 'ENOENT' });
    return cur;
  }

  /** The host path of `path`'s last name, in a checked directory. */
  private at(path: string): string {
    const names = this.names(path);
    if (!names.length) throw new MountSourceError('/: the root itself');
    try { return this.dir(names.slice(0, -1)) + '/' + names[names.length - 1]; } catch (e) { throw new MountSourceError(`${path}: ${strerror(e)}`); }
  }

  private static entry(name: string, st: any): MountEntry {
    const isDir = st.isDirectory();
    return { name, isDir, size: isDir ? 0 : Number(st.size), modified: Number(st.mtimeMs), version: `${st.mtimeNs}:${st.size}` };
  }

  private lstat(full: string): any | null {
    const st = this.fs.lstatSync(full, { bigint: true, throwIfNoEntry: false });
    return st && (st.isDirectory() || st.isFile()) ? st : null;
  }

  private check(path: string, full: string, expected: string | null): any | null {
    const st = this.lstat(full);
    const now = st === null ? null : LocalFolderSource.entry('', st).version!;
    if (now !== expected && !(st !== null && st.isDirectory() && expected !== null))
      throw new MountConflictError(`${path}: version ${now ?? 'None'}, expected ${expected ?? 'None'}`);
    return st;
  }

  list(path: string, maxEntries: number): MountEntry[] {
    const out: MountEntry[] = [];
    try {
      const where = this.dir(this.names(path));
      const d = this.fs.opendirSync(where);
      try {
        for (let n = 0, e; n < maxEntries * 2 + 64 && (e = d.readSync()); n++) { // links and devices are skipped, not counted
          const st = this.fs.lstatSync(where + '/' + e.name, { bigint: true });
          if (!st.isDirectory() && !st.isFile()) continue;
          out.push(LocalFolderSource.entry(e.name, st));
          if (out.length >= maxEntries) break;
        }
      } finally { d.closeSync(); }
    } catch (e) {
      if (e instanceof MountSourceError) throw e;
      throw new MountSourceError(`${path}: ${strerror(e)}`);
    }
    return out;
  }

  stat(path: string): MountEntry | null {
    if (path === '/') return { name: '', isDir: true };
    const st = this.lstat(this.at(path));
    return st === null ? null : LocalFolderSource.entry(nameOf(path), st);
  }

  read(path: string, maxBytes: number): Uint8Array {
    const fs = this.fs, c = fs.constants, full = this.at(path);
    let fd: number;
    // never blocks on a FIFO, never follows a link; then only a regular file is read
    try { fd = fs.openSync(full, c.O_RDONLY | (c.O_NOFOLLOW ?? 0) | (c.O_NONBLOCK ?? 0)); } catch (e) { throw new MountSourceError(`${path}: ${strerror(e)}`); }
    try {
      const st = fs.fstatSync(fd);
      if (!st.isFile()) throw new MountSourceError(`${path}: not a regular file`);
      if (st.size > maxBytes) throw new MountSourceError(msg('tooLarge'));
      const buf = new Uint8Array(Math.min(maxBytes + 1, 2 ** 31 - 1));
      let n = 0, got;
      while (n < buf.length && (got = fs.readSync(fd, buf, n, buf.length - n, null)) > 0) n += got;
      return buf.slice(0, n);
    } finally { fs.closeSync(fd); }
  }

  write(path: string, data: Uint8Array, expectedVersion: string | null): string {
    const fs = this.fs, c = fs.constants, full = this.at(path);
    this.check(path, full, expectedVersion);
    const tmp = full.slice(0, full.lastIndexOf('/') + 1) + `.tw-${[...crypto.getRandomValues(new Uint8Array(4))].map(b => b.toString(16).padStart(2, '0')).join('')}.tmp`;
    try {
      const fd = fs.openSync(tmp, c.O_WRONLY | c.O_CREAT | c.O_EXCL | (c.O_NOFOLLOW ?? 0), 0o644);
      try { fs.writeSync(fd, data); } finally { fs.closeSync(fd); }
      fs.renameSync(tmp, full);
    } catch (e) {
      try { fs.unlinkSync(tmp); } catch { /* not made */ }
      throw new MountSourceError(`${path}: ${strerror(e)}`);
    }
    return LocalFolderSource.entry('', fs.lstatSync(full, { bigint: true })).version!;
  }

  delete(path: string, expectedVersion: string | null) {
    const full = this.at(path);
    const st = this.check(path, full, expectedVersion);
    try { if (st !== null && st.isDirectory()) this.fs.rmdirSync(full); else this.fs.unlinkSync(full); } catch (e) { throw new MountSourceError(`${path}: ${strerror(e)}`); }
  }

  rename(path: string, to: string, expectedVersion: string | null): null {
    const full = this.at(path), dest = this.at(to);
    if (this.lstat(dest) !== null) throw new MountConflictError(`${to}: already exists`);
    this.check(path, full, expectedVersion);
    try { this.fs.renameSync(full, dest); } catch (e) { throw new MountSourceError(`${path}: ${strerror(e)}`); }
    return null;
  }

  mkdir(path: string): null {
    const full = this.at(path);
    try { this.fs.mkdirSync(full); } catch (e) { throw new MountSourceError(`${path}: ${strerror(e)}`); }
    return null;
  }
}

/** Files kept in memory: for tests, and as an example of a source. `files` maps paths ("notes/a.md"; a trailing "/" for an
 * empty directory) to text or bytes. set() and remove() change it from outside; each change takes the next version ("1",
 * "2", ...). hook(op, path), called before every operation (it may return a Promise), can sleep or throw to simulate latency,
 * failures and conflicts. calls lists every [op, path] asked. */
export class MemorySource implements MountSource {
  readonly type: string = 'memory';
  capabilities: Iterable<MountCapability> = CAPABILITIES;
  hook: ((op: string, path: string) => unknown) | null = null;
  readonly calls: [op: string, path: string][] = [];
  protected next = 0;
  protected readonly items = new Map<string, [content: Uint8Array | null, version: string]>([['/', [null, '0']]]);

  constructor(files: Record<string, string | Uint8Array | null> = {}, readonly id = 'memory', readonly modified: number = Date.UTC(2026, 0, 1)) {
    for (const p of Object.keys(files).sort(byCodePoint)) {
      if (p.endsWith('/')) this.mkdirs(VirtualFileSystem.normalize('/', p));
      else this.set(p, files[p] ?? '');
    }
  }

  protected version() { return String(++this.next); }
  private mkdirs(path: string) {
    if (this.items.has(path)) return;
    this.mkdirs(parentOf(path));
    this.items.set(path, [null, this.version()]);
  }
  private isDir(path: string) { return this.items.has(path) && this.items.get(path)![0] === null; }
  private missing(path: string) { return new MountSourceError(`${path}: No such file or directory`); }

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

  async list(path: string, maxEntries: number, _options?: MountCallOptions): Promise<MountEntry[]> {
    await this.enter('list', path);
    if (!this.isDir(path)) throw this.missing(path);
    const prefix = path.replace(/\/+$/, '') + '/';
    return [...this.items.keys()].filter(p => p !== '/' && p.startsWith(prefix) && !p.slice(prefix.length).includes('/')).slice(0, maxEntries).map(p => this.entry(p)!);
  }

  async stat(path: string, _options?: MountCallOptions): Promise<MountEntry | null> {
    await this.enter('stat', path);
    return this.entry(path);
  }

  async read(path: string, _maxBytes: number, _options?: MountCallOptions): Promise<Uint8Array> {
    await this.enter('read', path);
    const data = this.items.get(path)?.[0];
    if (!data) throw this.missing(path);
    return data;
  }

  async write(path: string, data: Uint8Array, expectedVersion: string | null, _options?: MountCallOptions): Promise<string> {
    await this.enter('write', path);
    this.check(path, expectedVersion);
    if (!this.isDir(parentOf(path))) throw this.missing(parentOf(path));
    const version = this.version();
    this.items.set(path, [data.slice(), version]);
    return version;
  }

  async delete(path: string, expectedVersion: string | null, _options?: MountCallOptions): Promise<void> {
    await this.enter('delete', path);
    if (this.isDir(path)) {
      if ([...this.items.keys()].some(p => p.startsWith(path + '/'))) throw new MountSourceError(`${path}: Directory not empty`);
    } else this.check(path, expectedVersion);
    this.items.delete(path);
  }

  async rename(path: string, to: string, expectedVersion: string | null, _options?: MountCallOptions): Promise<string | null> {
    await this.enter('rename', path);
    if (this.items.has(to)) throw new MountConflictError(`${to}: already exists`);
    if (!this.isDir(path)) this.check(path, expectedVersion);
    if (!this.isDir(parentOf(to))) throw this.missing(parentOf(to));
    for (const p of [...this.items.keys()]) {
      if (p !== path && !p.startsWith(path + '/')) continue;
      const item = this.items.get(p)!;
      this.items.delete(p);
      this.items.set(to + p.slice(path.length), item);
    }
    return null;
  }

  async mkdir(path: string, _options?: MountCallOptions): Promise<string | null> {
    await this.enter('mkdir', path);
    if (this.items.has(path)) throw new MountConflictError(`${path}: already exists`);
    if (!this.isDir(parentOf(path))) throw this.missing(parentOf(path));
    const version = this.version();
    this.items.set(path, [null, version]);
    return version;
  }
}

// ---------- changes ----------

/** One operation that makes the source match the mount: kind write, delete, rename or mkdir; path (and `to` for a rename)
 * within the source; version: what the entry was when read (null: it must not exist); content for a write. applyChanges
 * matches changes by value, content included. */
export interface MountChange {
  readonly kind: 'write' | 'delete' | 'rename' | 'mkdir';
  readonly path: string;
  readonly to: string | null;
  readonly version: string | null;
  readonly content: Uint8Array | null;
}

/** What applyChanges did: the changes applied, the one it stopped at (null if none) with the error (the source's full text:
 * for the host, not the agent), whether that was a conflict (the source changed since it was read), and the changes after
 * it that were not tried. */
export interface MountApplyResult {
  applied: MountChange[];
  failed: MountChange | null;
  error: string | null;
  conflict: boolean;
  notTried: MountChange[];
}

/** A mount as `shell.mounts` lists it. error: why it is unavailable or its listing failed (the source's full text; null when
 * it works); warnings: what its listings left out; held: how many changes from a loaded snapshot wait for acceptSnapshotChanges. */
export interface MountInfo {
  path: string;
  mode: MountMode;
  type: string;
  id: string;
  error: string | null;
  warnings: string[];
  held: number;
}

/** One question a recording kept and its answer: list (entries), stat (entry; null: missing), read (body), write, rename and
 * mkdir (version), delete; error (as the agent may see it) and conflict when the source refused. */
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

/** @internal A mount as a snapshot keeps it: `held` are a loaded file's changes waiting for the host; `listing` (in memory
 * only, never saved) lets a snapshot taken in this process come back without a source call. */
export interface MountState {
  path: string; mode: MountMode; type: string; id: string; changes: MountChange[]; held: MountChange[];
  listing?: Map<string, [Entry, boolean]>;
}

/** @internal */
export function mountChange(kind: MountChange['kind'], path: string, o: { to?: string | null; version?: string | null; content?: Uint8Array | null } = {}): MountChange {
  return { kind, path, to: o.to ?? null, version: o.version ?? null, content: o.content ?? null };
}

const sameBytes = (x: Uint8Array | null, y: Uint8Array | null) => x === y || (!!x && !!y && x.length === y.length && x.every((b, i) => b === y[i]));
const sameChange = (a: MountChange, b: MountChange) =>
  a.kind === b.kind && a.path === b.path && a.to === b.to && a.version === b.version && sameBytes(a.content, b.content);
const among = (c: MountChange, list: readonly MountChange[]) => list.some(x => sameChange(c, x));

// ---------- mounts ----------

/** @internal A listed entry that passed the checks: every field filled in (size null: unknown). */
export type Entry = { name: string; isDir: boolean; size: number | null; modified: number | null; version: string };

/** What the mount read from the source at one path: the entry, the node it became, and its content's hash once fetched. */
interface Base {
  entry: Entry;
  node: Node | null;
  digest: string | null;
  clock: number | null; // the node's change clock right after it was fetched: unchanged since means clean, can be evicted
}

/** @internal A source call that failed: `safe` for the agent and recordings, `full` for the host. */
export class SourceFailure extends ShellError {
  constructor(readonly safe: string, readonly full: string) { super(safe); }
}

type Grow = [path: string, node: Dir, depth: number];

class Mount {
  type: string;
  id: string;
  base = new Map<string, Base>();
  expected = new Map<string, string | null>(); // versions a replayed change was made on, which win over base's
  absent = new Set<string>(); // paths a replayed write expects not to exist yet
  stale = false; // restored from a file: listed at the start of the next call
  held: MountChange[] = []; // changes not in the tree: from a loaded snapshot (quarantined), or while unavailable
  error: string | null = null; // the full text, for the host
  shown: string | null = null; // what the agent sees of it
  warnings: string[] = [];
  misses = new Map<Node, Node>(); // files and directories used before they were fetched or listed
  failed = new Map<Node, string>(); // why a file could not be fetched (safe to show)
  fetched = new Map<Node, Base>(); // in fetch order, for eviction
  stuck: MountChange[] = []; // live mode: changes that failed, not tried again until they change
  busy = false; // applying: a pipeline's other stage leaves it to the end of the call
  trusted = true; // held changes may be replayed at the next listing (a replay's), not only by the host
  lastSafe: string | null = null; // what the agent may see of the last apply's error

  readonly miss = (node: FileNode, path: string) => {
    if (this.mounts.items.get(this.path) !== this) throw new ShellError(`${path}: ${msg('removed')}`);
    this.misses.set(node, node);
    const reason = this.failed.get(node);
    if (reason === msg('tooLarge')) throw new ShellError(`${path}: ${reason}`);
    throw new ShellError(`${path}: ${reason ? reason + msg('retry') : msg('notFetched')}`);
  };

  /** A directory nothing listed yet was looked into: it is listed after the command. */
  readonly notListed = (node: Dir) => {
    if (this.mounts.items.get(this.path) === this) this.misses.set(node, node);
    return msg('notListed');
  };

  constructor(readonly mounts: Mounts, readonly path: string, readonly source: MountSource | null, readonly mode: MountMode) {
    this.type = String(source?.type ?? '');
    this.id = String(source?.id ?? '');
  }

  get fs() { return this.mounts.shell.fs; }
  get available() { return this.error === null && !this.stale && this.source !== null; }

  info(): MountInfo {
    return { path: this.path, mode: this.mode, type: this.type, id: this.id, error: this.error, warnings: [...this.warnings], held: this.held.length };
  }

  machine(p: string) { return p === '/' ? this.path : this.path + p; }

  capabilities(): Set<string> {
    const have = new Set<string>(this.source?.capabilities ?? []);
    return new Set(CAPABILITIES.filter(c => have.has(c)));
  }

  // ----- talking to the source -----

  /** Calls the source, bounded by the call's time limit (its signal aborted there), and keeps the exchange while recording.
   * Errors come out as MountConflictError, or SourceFailure with a text the agent may see. */
  async call(op: MountOp, path: string, args: unknown[] = [], to: string | null = null): Promise<any> {
    const shell = this.mounts.shell;
    const x = { mount: this.path, op, path, to, entries: null, entry: null, body: null, version: null, error: null, conflict: false };
    const control = new AbortController();
    const options: MountCallOptions = { signal: control.signal };
    let r: any;
    try {
      const fn = (this.source as any)?.[op];
      if (typeof fn !== 'function') throw new Error(`the source has no ${op}`);
      r = op === 'rename' ? fn.call(this.source, path, to, ...args, options) : fn.call(this.source, path, ...args, options);
      if (r && typeof r.then === 'function') {
        const left = shell.remainingMs();
        if (left === Infinity) r = await r;
        else {
          let timer: ReturnType<typeof setTimeout> | undefined;
          // +1: past the deadline, which checkTime compares strictly
          const late = new Promise((_, reject) => { timer = setTimeout(() => reject(TIMED_OUT), Math.max(left, 0) + 1); });
          try { r = await Promise.race([r, late]); } finally { clearTimeout(timer); }
        }
      }
    } catch (e) {
      if (e === TIMED_OUT) {
        control.abort();
        this.mounts.log({ ...x, error: msg('timedOut') });
        throw new SourceFailure(msg('timedOut'), msg('timedOut'));
      }
      if (isControl(e)) throw e;
      if (e instanceof MountConflictError) {
        this.mounts.log({ ...x, error: e.message || 'conflict', conflict: true });
        throw e;
      }
      const safe = safeText(e);
      this.mounts.log({ ...x, error: safe });
      throw new SourceFailure(safe, why(e));
    }
    if (op === 'list') {
      r = take(r ?? [], (args[0] as number) + 64); // a few bad names may be skipped; never read without end
      this.mounts.log({ ...x, entries: r });
    } else if (op === 'stat') this.mounts.log({ ...x, entry: r ?? null });
    else if (op === 'read') {
      if (!(r instanceof Uint8Array)) throw new SourceFailure(msg('notBytes'), msg('notBytes'));
      r = r.slice();
      this.mounts.log({ ...x, body: r });
    } else {
      r = r == null ? null : String(r).slice(0, MAX_VERSION);
      this.mounts.log({ ...x, version: r });
    }
    return r;
  }

  /** Lists the unlisted directories in `queue`, breadth first, then (deep) what they hold; no new directory is started past
   * `budget` entries or LIST_LIMITS.seconds, and what is left stays "not listed yet". */
  async grow(queue: Grow[], budget: number, deep = true) {
    const shell = this.mounts.shell, limit = shell.options.maxExtractEntries;
    const stop = Date.now() + LIST_LIMITS.seconds * 1000;
    let seen = 0;
    while (queue.length) {
      const [bp, node, depth] = queue.shift()!;
      if (!node.unlisted) continue;
      const left = limit - (this.base.size - 1); // a directory is listed whole (up to the entry limit), never in part
      if (left <= 0 || seen >= budget || Date.now() > stop) {
        if (this.base.size >= limit) this.warn(msg('truncated', { dir: this.path, limit: String(limit) }));
        return;
      }
      const got = await this.call('list', bp, [left]);
      const names = new Set<string>();
      let good: Entry[] = [], skipped = 0;
      for (const raw of got) {
        const e = valid(raw);
        if (!e || names.has(e.name)) { skipped++; continue; }
        names.add(e.name);
        good.push(e);
      }
      if (skipped) this.warn(msg('skipped', { dir: this.machine(bp), count: String(skipped) }));
      node.unlisted = null;
      good = good.sort((a, b) => byCodePoint(a.name, b.name));
      if (good.length > left) {
        good = good.slice(0, left);
        this.warn(msg('truncated', { dir: this.path, limit: String(limit) }));
      }
      for (const e of good) {
        const child = this.place(node, join(bp, e.name), e, depth + 1 < MAX_DEPTH);
        seen++;
        if (e.isDir && deep && depth + 1 < MAX_DEPTH) queue.push([join(bp, e.name), child as Dir, depth + 1]);
      }
      shell.checkTime();
    }
  }

  warn(w: string) { if (!this.warnings.includes(w)) this.warnings.push(w); }

  place(parent: Dir, p: string, e: Entry, listable: boolean): Node {
    const ro = this.mode === 'read-only';
    const owner = ro ? 'root' : this.mounts.shell.options.user;
    const modified = e.modified ?? this.fs.clock();
    let node: Node;
    if (e.isDir) {
      node = Object.assign(new Dir(), { modified, owner, mode: ro ? 0o555 : 0o755 });
      if (listable) (node as Dir).unlisted = this.notListed;
    } else node = Object.assign(new FileNode(), { modified, owner, mode: ro ? 0o444 : 0o644, reportedSize: e.size ?? 0, loader: this.miss });
    parent.children.set(nameOf(p), node);
    this.base.set(p, { entry: e, node, digest: null, clock: null });
    return node;
  }

  // ----- the files in the machine -----

  /** Replaces the mount point's tree: the root, unlisted, or the saved `entries` (source path -> [entry, unlisted]). */
  build(entries?: Map<string, [Entry, boolean]>): Dir {
    const fs = this.fs, ro = this.mode === 'read-only';
    dropChildren(fs, this.path);
    fs.mkdir(this.path, true);
    const root = fs.nodeAt(this.path) as Dir;
    Object.assign(root, ro ? { owner: 'root', mode: 0o555 } : { owner: this.mounts.shell.options.user, mode: 0o755 });
    root.unlisted = entries === undefined ? this.notListed : null;
    this.base = new Map([['/', { entry: { name: '', isDir: true, size: 0, modified: null, version: '' }, node: root, digest: null, clock: null }]]);
    this.fetched = new Map(); this.misses = new Map(); this.failed = new Map();
    this.expected = new Map(); this.absent = new Set();
    for (const [p, [e, unlisted]] of entries ?? []) {
      const parent = this.base.get(parentOf(p));
      if (p !== '/' && parent && parent.entry.isDir) {
        const node = this.place(parent.node as Dir, p, e, unlisted);
        if (!unlisted && node instanceof Dir) node.unlisted = null;
      }
    }
    this.error = this.shown = null;
    this.stale = false;
    return root;
  }

  /** An empty mount point the agent can't open; the changes are kept for a later mount. */
  unavailable(reason: string | null, shown?: string | null) {
    const fs = this.fs;
    dropChildren(fs, this.path);
    fs.mkdir(this.path, true);
    fs.chmod(this.path, 0);
    this.base = new Map();
    this.error = reason;
    this.shown = shown ?? reason;
  }

  /** Lists the unlisted directories on the way to a machine path, and the path itself if it is one (deep: all below). */
  async reveal(path: string, deep: boolean, budget = REVEAL_BUDGET) {
    let node: Node | null | undefined = this.fs.nodeAt(this.path, false);
    const at = new Map<Node, string>();
    for (const [p, b] of this.base) if (b.node) at.set(b.node, p);
    const rest: (string | null)[] = path !== this.path ? path.slice(this.path.length).split('/').slice(1) : [];
    for (const name of [...rest, null]) {
      if (!(node instanceof Dir)) return;
      const bp = at.get(node);
      if (node.unlisted && bp !== undefined) {
        await this.grow([[bp, node, bp !== '/' ? bp.split('/').length - 1 : 0]], budget, deep && name === null);
        for (const [p, b] of this.base) if (b.node) at.set(b.node, p); // the directories it just placed, for the next name
      } else if (name === null && deep) {
        const queue: Grow[] = dirsUnder(node).filter(n => n.unlisted && at.has(n)).map(n => [at.get(n)!, n, 0]);
        await this.grow(queue, budget);
      }
      if (name === null) return;
      node = node.children.get(name);
    }
  }

  /** Makes `changes` again on the tree, remembering the versions they were made on. */
  replay(changes: readonly MountChange[]) {
    const fs = this.fs;
    for (const c of changes) {
      const path = this.machine(c.path);
      try {
        if (c.kind === 'write') { fs.mkdir(parentOf(path), true); store(fs, path, c.content ?? new Uint8Array(0)); }
        else if (c.kind === 'mkdir') fs.mkdir(path, true);
        else if (c.kind === 'rename') fs.move(path, this.machine(c.to!));
        else if (c.kind === 'delete') fs.delete(path, true);
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        this.warn(`${path}: ${e.message}`);
      }
      if (c.kind === 'write' && c.version === null) this.absent.add(c.path);
      else if (c.kind !== 'mkdir' && !this.expected.has(c.path)) this.expected.set(c.path, c.version);
    }
  }

  async revealFor(changes: readonly MountChange[]) {
    for (const c of changes) for (const p of [c.path, c.to]) if (p !== null) await this.reveal(this.machine(parentOf(p)), false);
  }

  /** Fetches placeholders, FETCH_AT_ONCE at a time, making room by evicting clean fetched files; failures are kept for miss().
   * When the call's time runs out first, says how far it got. */
  async fetch(nodes: Node[], err?: Writer, where = '') {
    const fs = this.fs, shell = this.mounts.shell;
    const at = new Map<Node, [string, Base]>();
    for (const [p, b] of this.base) if (b.node) at.set(b.node, [p, b]);
    const todo = nodes.filter((n): n is FileNode => n instanceof FileNode && !!n.loader && at.has(n));
    let done = 0;
    for (let i = 0; i < todo.length; i += FETCH_AT_ONCE) {
      if (shell.remainingMs() <= 0) {
        err?.write('mount: ' + msg('progress', { count: String(done), total: String(todo.length), dir: where }) + '\n');
        return;
      }
      const batch = todo.slice(i, i + FETCH_AT_ONCE);
      let room = fs.capacity - fs.usedBytes;
      const need = batch.reduce((s, n) => s + (at.get(n)![1].entry.size ?? 0), 0);
      if (need > room) room += this.evict(need - room);
      const share = Math.max(Math.floor(room / batch.length), 0);
      const results = await Promise.allSettled(batch.map(n => this.fetchOne(...at.get(n)!, share)));
      for (const [k, node] of batch.entries()) {
        const r = results[k];
        if (r.status === 'rejected') {
          if (isControl(r.reason)) throw r.reason;
          this.failed.set(node, r.reason instanceof SourceFailure ? r.reason.safe : why(r.reason));
          continue;
        }
        const b = at.get(node)![1];
        if (!node.loader) continue; // written while we waited
        if (r.value.length > fs.capacity - fs.usedBytes) { this.failed.set(node, msg('tooLarge')); continue; }
        fill(fs, node, r.value);
        b.digest = sha1(r.value); b.clock = node.changed;
        this.failed.delete(node);
        this.fetched.set(node, b);
        done++;
      }
    }
  }

  async fetchOne(p: string, b: Base, room: number): Promise<Uint8Array> {
    if ((b.entry.size ?? 0) > room) throw new SourceFailure(msg('tooLarge'), msg('tooLarge'));
    const data: Uint8Array = await this.call('read', p, [room]);
    if (data.length > room) throw new SourceFailure(msg('tooLarge'), msg('tooLarge'));
    return data;
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
      n.reportedSize = b.entry.size ?? 0; n.loader = this.miss;
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

  /** The agent's changes, in an order that applies one after another: deletes of names about to be replaced, renames and
   * mkdirs as the new tree has them (parents first, moving a name out of the way through a temporary name when it is
   * taken), then writes, then the remaining deletes (children first). A file still only a placeholder somewhere in the
   * machine (moved out of the mount) is not deleted until it is fetched. */
  changes(): MountChange[] {
    if (!this.available) return [];
    const root = this.fs.nodeAt(this.path, false);
    if (!(root instanceof Dir) || this.base.size === 0) return [];
    const inTree = nodesOf(root);
    const at = new Map<Node, string>();
    for (const [p, b] of this.base) if (p !== '/' && b.node) at.set(b.node, p);
    const where = new Map<Node, string>(); // node -> its path in the new tree
    for (const [n, p] of walkPaths(root, '/')) if (!where.has(n)) where.set(n, p);
    const gone = [...this.base].filter(([p, b]) => p !== '/' && !inTree.has(b.node!)).map(([p]) => p).sort(byCodePoint).reverse();
    const held = new Set(gone.filter(p => { const n = this.base.get(p)!.node; return n instanceof FileNode && n.nlink > 0 && !!n.loader; }));
    for (const p of held) { const n = this.base.get(p)!.node!; this.misses.set(n, n); } // fetched after the next command, then deleted
    for (const p of gone) if ([...held].some(q => q.startsWith(p + '/'))) held.add(p);
    const dropped = new Set(held), visited = new Set<Node>(), movedDone = new Set<string>();
    const ops: MountChange[] = [], writes: MountChange[] = [];
    const renames: [from: string, to: string][] = []; // as applied so far, for where a base path is now
    let swap = 0;
    const nowAt = (p: string) => {
      for (const [f, t] of renames) if (p === f || p.startsWith(f + '/')) p = t + p.slice(f.length);
      return p;
    };
    // the base path whose entry is at source path p now (renames undone)
    const baseAt = (p: string) => {
      for (let i = renames.length - 1; i >= 0; i--) { const [f, t] = renames[i]; if (p === t || p.startsWith(t + '/')) p = f + p.slice(t.length); }
      return p;
    };
    const writeVersion = (p: string) => this.absent.has(p) ? null : this.oldVersion(p);
    // q is gone and something else stands where it was: delete it, and what was below it, first. Entries below it that
    // still exist elsewhere and move later go to a temporary name at the top first.
    const drop = (q: string) => {
      const keep = [...this.base].filter(([g, b]) => g.startsWith(q + '/') && inTree.has(b.node!) && !movedDone.has(g)).map(([g]) => g).sort(byCodePoint);
      for (const g of keep) {
        if (keep.some(k => g.startsWith(k + '/'))) continue;
        const tmp = `/.topwater-move-${++swap}`;
        ops.push(mountChange('rename', nowAt(g), { to: tmp, version: this.oldVersion(g) }));
        renames.push([nowAt(g), tmp]);
      }
      for (const g of gone)
        if ((g === q || g.startsWith(q + '/')) && !dropped.has(g)) { dropped.add(g); ops.push(mountChange('delete', nowAt(g), { version: this.oldVersion(g) })); }
    };
    // source path p is about to be taken: a base entry still there that moves later goes to a temporary name
    const clear = (p: string) => {
      const q = baseAt(p), b = this.base.get(q);
      if (b && !movedDone.has(q) && inTree.has(b.node!) && where.get(b.node!) !== p) {
        const tmp = `${p}.topwater-swap-${++swap}`;
        ops.push(mountChange('rename', p, { to: tmp, version: this.oldVersion(q) }));
        renames.push([p, tmp]);
      }
    };
    const walk = (d: Dir, path: string, was: string | null) => {
      for (const name of [...d.children.keys()].sort(byCodePoint)) {
        const n = d.children.get(name)!;
        if (!(n instanceof Dir) && !(n instanceof FileNode)) continue; // symbolic links are not part of a change set
        const p = join(path, name);
        let bp = at.get(n) ?? null;
        if (visited.has(n)) bp = null; // a second hard link
        visited.add(n);
        const orig = was !== null ? join(was, name) : null;
        const old = orig !== null ? this.base.get(orig) : undefined;
        const replaced = old !== undefined && !inTree.has(old.node!);
        if (bp !== null) {
          if (bp !== orig) {
            if (replaced) drop(orig!);
            clear(p);
            ops.push(mountChange('rename', nowAt(bp), { to: p, version: this.oldVersion(bp) }));
            renames.push([nowAt(bp), p]);
          }
          movedDone.add(bp);
          const b = this.base.get(bp)!;
          if (n instanceof Dir) walk(n, p, bp);
          else if (!n.loader && n.changed !== b.clock && sha1(n.raw) !== b.digest)
            writes.push(mountChange('write', p, { version: writeVersion(bp), content: n.raw }));
          continue;
        }
        if (replaced && (n instanceof FileNode) === !old!.entry.isDir) {
          dropped.add(orig!); // a file replaced by a file is a write; a directory made again is the same one
          if (n instanceof Dir) walk(n, p, orig);
          else if (!n.loader) writes.push(mountChange('write', p, { version: writeVersion(orig!), content: n.raw }));
          continue;
        }
        if (replaced) drop(orig!);
        if (n instanceof Dir) {
          clear(p); // a write needs no room yet: writes come after every rename
          ops.push(mountChange('mkdir', p));
          walk(n, p, null);
        } else if (!n.loader) { // shortcut: a placeholder moved in from another mount is left out
          const version = this.base.has(p) || this.absent.has(p) ? null : this.expected.get(p) ?? null;
          writes.push(mountChange('write', p, { version, content: n.raw }));
        }
      }
    };
    walk(root, '/', '/');
    const deletes = gone.filter(p => !dropped.has(p)).map(p => mountChange('delete', nowAt(p), { version: this.oldVersion(p) }));
    return [...ops, ...writes, ...deletes];
  }

  async apply(changes?: readonly MountChange[] | null): Promise<MountApplyResult> {
    const current = this.changes();
    const todo = changes == null ? current : changes.filter(c => among(c, current));
    if (!this.available)
      return { applied: [], failed: todo[0] ?? null, error: this.error ?? msg('notListedYet'), conflict: false, notTried: todo.slice(1) };
    const caps = this.capabilities();
    const applied: MountChange[] = [];
    this.busy = true;
    try {
      for (const [i, c] of todo.entries()) {
        let version: string | null = null;
        try {
          if (this.mode === 'read-only') throw new SourceFailure(msg('readOnly'), msg('readOnly'));
          if (!caps.has(c.kind)) throw new SourceFailure(msg('cannot', { kind: c.kind }), msg('cannot', { kind: c.kind }));
          const now: MountEntry | null = await this.call('stat', c.path);
          if (c.kind === 'mkdir' || (c.kind !== 'write' && now && now.isDir)) {
            if (!!now !== (c.kind !== 'mkdir')) // directories: only whether they exist
              throw new MountConflictError(`${c.path}: ${now ? 'already exists' : 'missing'}`);
          } else if ((now ? now.version ?? '' : null) !== c.version)
            throw new MountConflictError(`${c.path}: version ${now ? now.version ?? '' : 'None'}, expected ${c.version ?? 'None'}`);
          if (c.kind === 'rename' && await this.call('stat', c.to!)) throw new MountConflictError(`${c.to}: already exists`);
          if (c.kind === 'write') version = await this.call('write', c.path, [c.content ?? new Uint8Array(0), c.version]);
          else if (c.kind === 'delete') await this.call('delete', c.path, [c.version]);
          else if (c.kind === 'rename') version = await this.call('rename', c.path, [c.version], c.to);
          else version = await this.call('mkdir', c.path);
        } catch (e) {
          if (e instanceof MountConflictError) {
            this.lastSafe = null;
            return { applied, failed: c, error: e.message || 'conflict', conflict: true, notTried: todo.slice(i + 1) };
          }
          if (e instanceof SourceFailure) {
            this.lastSafe = e.safe;
            return { applied, failed: c, error: e.full, conflict: false, notTried: todo.slice(i + 1) };
          }
          throw e;
        }
        this.applied(c, version);
        applied.push(c);
      }
    } finally { this.busy = false; }
    return { applied, failed: null, error: null, conflict: false, notTried: [] };
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
      const b = this.base.get(c.to!);
      if (b) b.entry = { ...b.entry, name: nameOf(c.to!), version: version ?? b.entry.version };
      return;
    }
    const node = this.fs.nodeAt(this.machine(c.path), false);
    const now = this.fs.clock();
    if (c.kind === 'mkdir') {
      this.base.set(c.path, { entry: { name: nameOf(c.path), isDir: true, size: 0, modified: now, version: version || '' }, node, digest: null, clock: null });
      return;
    }
    const data = c.content ?? new Uint8Array(0);
    const b: Base = { entry: { name: nameOf(c.path), isDir: false, size: data.length, modified: now, version: String(version || '') }, node, digest: sha1(data), clock: null };
    this.base.set(c.path, b);
    if (node instanceof FileNode) b.clock = sha1(node.raw) === b.digest ? node.changed : null;
  }
}

const TIMED_OUT = Symbol('timed out');

/** A shell's mounts (Shell.mount and friends call these), the sources the host mounted (by type and id, with the mode it
 * chose), and, while recording, the source exchanges of the current call. @internal */
export class Mounts {
  items = new Map<string, Mount>();
  sources = new Map<string, [MountSource, MountMode]>(); // type\0id -> the source and the host's mode
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

  /** The source and mode a restored mount gets: the host's (never the file's mode), or a replay's answers. */
  sourceFor(type: string, id: string, path: string, mode: MountMode): [MountSource | null, MountMode] {
    if (this.replay !== null) return [new ReplaySource(this.replay, path, type, id), mode];
    return this.sources.get(type + '\0' + id) ?? [null, mode];
  }

  // ----- the host's API -----

  async mount(path: string, source: MountSource, mode: MountMode = 'read-only') {
    if (!MOUNT_MODES.includes(mode)) throw new Error(`${mode}: the mode is one of ${MOUNT_MODES.join(', ')}`);
    if (typeof path !== 'string' || !path.startsWith('/')) throw new Error(`${path}: a mount point is an absolute path other than /`);
    path = VirtualFileSystem.normalize('/', path);
    if (path === '/') throw new Error(`${path}: a mount point is an absolute path other than /`);
    if (mode !== 'read-only' && !new Set<string>(source.capabilities ?? []).has('write'))
      throw new Error(`${path}: the source can't write, so it can only be mounted read-only`);
    const old = this.items.get(path);
    if (old) {
      if (old.source !== null || old.type !== String(source.type ?? '') || old.id !== String(source.id ?? ''))
        throw new Error(`${path}: already a mount point; unmount it first`);
    } else {
      for (const p of this.items.keys())
        if (path.startsWith(p + '/') || p.startsWith(path + '/')) throw new Error(`${path}: inside another mount, or holds one`);
      const fs = this.shell.fs;
      if (fs.exists(path, false) && !(fs.isDirectory(path, false) && fs.list(path).length === 0))
        throw new Error(`${path}: exists and is not an empty directory`);
    }
    const m = new Mount(this, path, source, mode);
    if (old) m.held = old.held; // still the host's to accept
    const root = m.build();
    try {
      await m.grow([['/', root, 0]], LIST_LIMITS.entries);
    } catch (e) {
      if (old) old.unavailable(old.error, old.shown); else dropChildren(this.shell.fs, m.path);
      if (e instanceof SourceFailure) throw new Error(`${path}: ${e.full}`);
      throw e;
    }
    this.items.set(path, m);
    this.sources.set(m.type + '\0' + m.id, [source, mode]);
  }

  unmount(path: string) {
    const m = this.get(path), fs = this.shell.fs;
    this.items.delete(m.path);
    if (![...this.items.values()].some(o => o.type === m.type && o.id === m.id)) this.sources.delete(m.type + '\0' + m.id);
    dropChildren(fs, m.path);
    fs.chmod(m.path, 0o755);
  }

  async refresh(path: string) {
    const m = this.get(path);
    if (!m.source) throw new SourceFailure(m.shown ?? 'unavailable', m.error ?? 'unavailable');
    const changes = m.changes();
    const root = m.build();
    await m.grow([['/', root, 0]], LIST_LIMITS.entries);
    await m.revealFor(changes);
    m.replay(changes);
  }

  /** Drops the agent's changes (and, with held, a loaded snapshot's): the files are the source's again. */
  discard(path: string, held = true) {
    const m = this.get(path);
    if (held) m.held = [];
    if (m.available) m.build(listingOf(m));
  }

  /** Makes a loaded snapshot's changes the agent's: in the files, and in pendingChanges. */
  async accept(path: string) {
    const m = this.get(path);
    if (!m.available) throw new Error(`${path}: ${m.error || 'not listed yet'}; mount it, or run a call first`);
    const changes = m.held;
    m.held = [];
    await m.revealFor(changes);
    m.replay(changes);
  }

  // ----- around commands -----

  /** At the start of a call: lists the mounts a restore brought back, within the listing budget. A listing that fails leaves
   * the mount unavailable for this call (tried again at the next); the call itself goes on. */
  async prepare() {
    for (const m of [...this.items.values()]) {
      if (!(m.stale && m.source)) continue;
      try {
        const root = m.build();
        await m.grow([['/', root, 0]], LIST_LIMITS.entries);
      } catch (e) {
        if (!(e instanceof SourceFailure)) throw e;
        m.unavailable(e.full, e.safe);
        m.stale = true;
        continue;
      }
      if (m.trusted && m.held.length) {
        const changes = m.held;
        m.held = [];
        await m.revealFor(changes);
        m.replay(changes);
      }
    }
  }

  private named(w: string): string[] {
    const shell = this.shell, out: string[] = [];
    for (const part of w.startsWith('-') && w.includes('=') ? [w, w.slice(w.indexOf('=') + 1)] : [w])
      if (part) { const p = shell.resolvePath(part); out.push(shell.agentFs.realPath(p, false) ?? p); }
    return out;
  }

  /** Lists and fetches what a command is about to use: the paths it names (a directory: one level, or everything below for a
   * command that walks it), the file it redirects from or appends to, and the working directory only for a recursive grep
   * given no path. Commands that read no content only list. A file moved out of its mount by `mv` is fetched first, so its
   * content is never lost. */
  async before(name: string, argv: string[], err?: Writer, redirect?: string) {
    const shell = this.shell, fs = shell.agentFs;
    const paths = (redirect !== undefined ? [redirect] : argv.slice(1)).flatMap(w => this.named(w));
    if (redirect === undefined && GREPS.has(name) && recursive(argv) && !paths.some(p => this.find(p) && fs.exists(p, false)))
      paths.push(fs.realPath(shell.cwdPath) ?? shell.cwdPath);
    if (redirect === undefined && argv[0].includes('/')) paths.push(...this.named(argv[0])); // a script run by its path
    const deep = DEEP.has(name) || recursive(argv);
    const content = redirect !== undefined || !NO_CONTENT.has(name);
    let outOf = new Set<string>();
    if (name === 'mv' && paths.length >= 2) {
      const dest = this.find(paths[paths.length - 1]);
      outOf = new Set(paths.slice(0, -1).filter(p => { const m = this.find(p); return m !== null && m !== dest; }));
    }
    for (const p of paths) {
      const m = this.find(p);
      if (!m || !m.available) continue;
      await m.reveal(p, deep || outOf.has(p));
      if ((content || outOf.has(p)) && fs.exists(p, false)) await m.fetch(m.stubsUnder(p), err, p);
    }
  }

  /** After a command: lists and fetches what it missed. */
  async after() {
    for (const m of [...this.items.values()]) {
      if (!m.misses.size || !m.available) continue;
      const nodes = [...m.misses.values()];
      m.misses = new Map();
      for (const n of nodes) {
        if (!(n instanceof Dir) || !n.unlisted) continue;
        const bp = [...m.base].find(([, b]) => b.node === n)?.[0];
        if (bp !== undefined) await m.grow([[bp, n, bp.split('/').length - 1]], LIST_LIMITS.entries, false);
      }
      await m.fetch(nodes.filter(n => n instanceof FileNode));
    }
  }

  /** Live mode: applies the changes made so far (after a command's redirections are written, and when the call ends). A
   * change that fails is reported once and left pending; the changes after it are still tried, and it is tried again only
   * once it changes. */
  async settle(err: Writer) {
    for (const m of [...this.items.values()]) {
      if (m.mode !== 'live' || !m.available || m.busy) continue;
      let todo = m.changes().filter(c => !among(c, m.stuck));
      while (todo.length) {
        const r = await m.apply(todo);
        if (!r.failed) break;
        m.stuck.push(r.failed);
        const where = m.machine(r.failed.path);
        const reason = m.lastSafe ?? msg('sourceFailed');
        err.write('mount: ' + (r.conflict ? msg('conflict', { path: where, dir: m.path }) : msg('failed', { path: where, reason, dir: m.path })) + '\n');
        todo = r.notTried;
      }
      const current = m.changes();
      m.stuck = m.stuck.filter(c => among(c, current)); // applied, discarded or changed since: no longer stuck
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

  /** Each mount's definition and changes, and (in memory only, never saved) its listing, so restoring the snapshot in this
   * process needs no source call and keeps the agent's changes. */
  capture(): MountState[] {
    return [...this.items.values()].map(m => ({
      path: m.path, mode: m.mode, type: m.type, id: m.id, changes: m.changes(), held: [...m.held], ...m.available && { listing: listingOf(m) },
    }));
  }

  /** After the files were replaced: the snapshot's mounts with the host's sources and modes. A snapshot taken in this process
   * brings its listing and changes back as they were; one loaded from a file is listed at the next call, and its changes
   * wait for the host (acceptSnapshotChanges), never applied or shown as the agent's on their own. */
  restore(saved: readonly MountState[]) {
    this.items = new Map();
    for (const d of saved) {
      const [source, mode] = this.sourceFor(d.type, d.id, d.path, d.mode);
      const m = new Mount(this, d.path, source, mode);
      m.type = d.type; m.id = d.id;
      this.items.set(m.path, m);
      const listing = d.listing;
      m.held = [...d.held ?? [], ...(listing !== undefined && source ? [] : d.changes)];
      m.trusted = this.replay !== null; // a replay's sources are its recorded answers: nothing real to protect
      if (!source) m.unavailable(msg('unavailable', { source: `${m.type}:${m.id}` }));
      else if (listing !== undefined) { m.build(listing); m.replay(d.changes); }
      else { m.unavailable(null); m.stale = true; }
    }
  }

  /** What the mounts hold when a recording starts, as the exchanges a replay's first listing and reads answer from. */
  startExchanges(): MountExchange[] {
    const out: MountExchange[] = [];
    const x = { to: null, entries: null, entry: null, body: null, version: null, error: null, conflict: false };
    for (const m of this.items.values()) {
      if (!m.available) continue;
      const kids = new Map<string, MountEntry[]>();
      for (const [p, b] of m.base) if (b.entry.isDir && !(b.node instanceof Dir && b.node.unlisted)) kids.set(p, []);
      for (const [p, b] of m.base) if (p !== '/' && kids.has(parentOf(p))) kids.get(parentOf(p))!.push(b.entry);
      for (const [p, b] of m.base) {
        if (kids.has(p)) out.push({ ...x, mount: m.path, op: 'list', path: p, entries: kids.get(p)! });
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
    if (!q?.length) throw new MountSourceError(msg('replay', { op, path }));
    const x = q.length > 1 ? q.shift()! : q[0]; // the last answer repeats
    if (x.conflict) throw new MountConflictError(x.error ?? '');
    if (x.error !== null) throw new MountSourceError(x.error);
    return x;
  }

  list(path: string) { return [...this.answer('list', path).entries ?? []]; }
  stat(path: string) { return this.answer('stat', path).entry; }
  read(path: string) { return this.answer('read', path).body ?? new Uint8Array(0); }
  write(path: string) { return this.answer('write', path).version ?? ''; }
  delete(path: string) { this.answer('delete', path); }
  rename(path: string, to: string) { return this.answer('rename', path, to).version; }
  mkdir(path: string) { return this.answer('mkdir', path).version; }
}

// ---------- helpers ----------

const key = (op: string, path: string, to: string | null) => `${op}\0${path}\0${to ?? ''}`;

function safeText(e: unknown): string {
  if (e instanceof MountSourceError) return e.message || msg('sourceFailed');
  if (e instanceof SourceFailure) return e.safe;
  return msg('sourceFailed');
}

/** At most `n` items of what a source returned (an array or any iterable). */
function take(items: Iterable<unknown>, n: number): any[] {
  const out: unknown[] = [];
  if (n <= 0) return out;
  for (const x of items) { out.push(x); if (out.length >= n) break; }
  return out;
}

function recursive(argv: readonly string[]): boolean {
  return argv.slice(1).some(a => a === '--recursive' || a === '--dereference-recursive' || (a.startsWith('-') && !a.startsWith('--') && (a.includes('r') || a.includes('R'))));
}

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
  const { name, isDir, size = null, modified = null, version = '' } = e as MountEntry;
  if (typeof name !== 'string' || typeof version !== 'string') return null;
  if (name === '' || name === '.' || name === '..' || name.includes('/') || name.includes('\0') || new TextEncoder().encode(name).length > MAX_NAME_BYTES) return null;
  if (size !== null && !(Number.isSafeInteger(size) && size >= 0)) return null;
  if (version.length > MAX_VERSION) return null;
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

function* walkPaths(d: Dir, path: string): Generator<[Node, string]> {
  for (const name of [...d.children.keys()].sort(byCodePoint)) {
    const n = d.children.get(name)!, p = join(path, name);
    yield [n, p];
    if (n instanceof Dir) yield* walkPaths(n, p);
  }
}

function dirsUnder(d: Dir): Dir[] {
  const out: Dir[] = [], queue: Dir[] = [d];
  while (queue.length) {
    const n = queue.shift()!;
    out.push(n);
    for (const k of [...n.children.keys()].sort(byCodePoint)) { const c = n.children.get(k); if (c instanceof Dir) queue.push(c); }
  }
  return out;
}

function listingOf(m: Mount): Map<string, [Entry, boolean]> {
  const out = new Map<string, [Entry, boolean]>();
  for (const [p, b] of m.base) if (p !== '/') out.set(p, [b.entry, b.node instanceof Dir && !!b.node.unlisted]);
  return out;
}

/** Empties a directory (as root), whatever is listed or not in it. */
function dropChildren(fs: VirtualFileSystem, path: string) {
  const n = fs.nodeAt(path, false);
  if (n instanceof Dir) {
    n.unlisted = null;
    for (const name of [...n.children.keys()]) {
      const child = n.children.get(name);
      if (child instanceof Dir) for (const d of dirsUnder(child)) d.unlisted = null;
      fs.delete(join(path, name), true);
    }
  } else if (n) fs.delete(path, true);
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
