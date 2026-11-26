import { ERRNO } from './spec.generated.js';
import { decodeBytes, encodeText, hasEscapes, utf8Length, bytesToText } from './bytes.js';

/** Thrown by commands and the filesystem; becomes "cmd: message" on stderr with the given exit code. */
export class ShellError extends Error {
  constructor(message: string, public readonly exitCode = 1) { super(message); }
}

/** A bad command line: printed with GNU's "Try 'cmd --help'" hint; exits 1, or 2 for ls, grep, sort and diff. */
export class UsageException extends ShellError {
  constructor(message: string) { super(message, 1); }
}

export interface FileStat {
  isDirectory: boolean;
  size: number;
  /** Modification time, ms since the epoch (UTC). */
  modified: number;
  /** Unix permission bits, e.g. 0o755 (plus 0o1000 for sticky). */
  mode: number;
  owner: string;
  /** Hard links: 1 for a file (more after `ln`); 2 plus one per subdirectory for a directory, as on ext4. */
  links: number;
  /** 512-byte blocks in use, as `stat %b` and `ls -s` count them: whole 4 KiB filesystem blocks (none for a link). */
  blocks: number;
  /** For a symbolic link (a stat that didn't follow it): the path it holds. Its size is that path's length in bytes. */
  linkTarget: string | null;
  isSymlink: boolean;
}

/** Counts changes to every filesystem's nodes, so a change tracker visits only what moved since it last looked. */
let changeClock = 0;

abstract class Node {
  private _modified = Date.now();
  private _owner = 'root';
  private _mode = 0;
  private _nlink = 1;
  /** when this node last changed, on changeClock */
  changed = ++changeClock;
  get modified() { return this._modified; }
  set modified(v: number) { this._modified = v; this.changed = ++changeClock; }
  get owner() { return this._owner; }
  set owner(v: string) { this._owner = v; this.changed = ++changeClock; }
  get mode() { return this._mode; }
  set mode(v: number) { this._mode = v; this.changed = ++changeClock; }
  /** names this file or link has (hard links); unused for directories */
  get nlink() { return this._nlink; }
  set nlink(v: number) { this._nlink = v; this.changed = ++changeClock; }
}
/** A directory's names; adding, removing or replacing one marks the directory's list changed. */
class Names extends Map<string, Node> {
  constructor(private readonly owner: Dir) { super(); }
  override set(name: string, node: Node) { super.set(name, node); this.owner.listed = ++changeClock; return this; }
  override delete(name: string) { const had = super.delete(name); if (had) this.owner.listed = ++changeClock; return had; }
  override clear() { super.clear(); this.owner.listed = ++changeClock; }
}
class Dir extends Node {
  /** when a name was last added, removed or replaced */
  listed = ++changeClock;
  readonly children: Map<string, Node> = new Names(this);
}
class LinkNode extends Node {
  private _target = '';
  get target() { return this._target; }
  set target(v: string) { this._target = v; this.changed = ++changeClock; }
}
class FileNode extends Node {
  /** Text content (null when `data` holds the bytes, or while `chunks` holds pending appends). */
  private text: string | null = '';
  private chunks: string[] | null = null; // `>>` writes accumulate here, so append loops stay linear
  private _data: Uint8Array | null = null;
  private _reportedSize: number | null = null;
  private _size = 0;
  /** set for binary files; text is then unused */
  get data() { return this._data; }
  set data(v: Uint8Array | null) { this._data = v; this.changed = ++changeClock; }
  /** what `ls -l` shows for a program stub */
  get reportedSize() { return this._reportedSize; }
  set reportedSize(v: number | null) { this._reportedSize = v; this.changed = ++changeClock; }
  /** real bytes held, counted against the capacity */
  get size() { return this._size; }
  set size(v: number) { this._size = v; this.changed = ++changeClock; }

  get content(): string {
    if (this.text === null) { this.text = this.chunks!.join(''); this.chunks = null; }
    return this.text;
  }
  set content(v: string) { this.text = v; this.chunks = null; this.changed = ++changeClock; }
  get raw(): Uint8Array { return this.data ?? encodeText(this.content); }

  store(text: string | null, bytes: Uint8Array | null, append: boolean, reserve: (delta: number) => void) {
    if (bytes === null && text !== null && hasEscapes(text)) bytes = encodeText(text); // binary that came through a pipe
    if (bytes !== null || (append && this.data !== null)) {
      bytes ??= encodeText(text!);
      const size = append ? this.size + bytes.length : bytes.length;
      reserve(size - this.size);
      if (append) {
        const old = this.raw;
        const merged = new Uint8Array(old.length + bytes.length);
        merged.set(old); merged.set(bytes, old.length);
        this.data = merged;
      } else this.data = bytes;
      this.content = '';
      this.size = size;
      return;
    }
    const size = (append ? this.size : 0) + utf8Length(text!);
    reserve(size - this.size);
    this.size = size;
    if (append) {
      if (this.chunks === null) this.chunks = [this.content];
      this.chunks.push(text!);
      this.text = null;
    } else this.content = text!;
    this.data = null;
  }
}

export const DEFAULT_FILE_MODE = 0o644;
export const DEFAULT_DIR_MODE = 0o755;
export const NO_SPACE = ERRNO.ENOSPC;

/** One file, directory or symbolic link as a snapshot holds it. @internal */
export interface VfsEntry {
  path: string; isDirectory: boolean; mode: number; owner: string; modified: number;
  /** a symbolic link's target */
  linkTarget: string | null;
  /** an earlier entry's path that this one is another name of (the entry still carries the content) */
  hardLink: string | null;
  /** a text file's content; null for directories and binary files */
  text: string | null;
  /** a binary file's exact bytes */
  data: Uint8Array | null;
  /** what `ls -l` shows for a program stub */
  reportedSize: number | null;
}

/** The state every view of one filesystem shares. */
interface Store { root: Dir; capacity: number; usedBytes: number; clock: () => number }

/**
 * In-memory Unix-style filesystem with owners and permission bits. Calls from your app are unrestricted (root).
 * While the shell runs the agent's commands it runs them as the agent (a view made by `as`), so the agent is an ordinary user:
 * it can't write to root-owned places such as /etc or /usr/bin. New files belong to the owner of their directory
 * (or to the agent, when it creates them). Binary files keep their exact bytes; the text API reads them as lossy UTF-8.
 * shortcut: no groups; add them if group permissions are needed.
 */
export class VirtualFileSystem {
  private readonly store: Store;
  /** The user this view acts as, or null for unrestricted access (your app). */
  readonly runAs: string | null = null;
  /** Permission bits taken away from new files and directories (the shell's `umask`). */
  umask = 0o022;

  constructor() { this.store = { root: Object.assign(new Dir(), { mode: DEFAULT_DIR_MODE }), capacity: Number.MAX_SAFE_INTEGER, usedBytes: 0, clock: Date.now }; }

  /**
   * A view of the same files that acts as `user` (null lifts the restrictions). The shell hands its commands such a
   * view of the agent, so whose call it is travels with the object instead of an ambient async context.
   */
  as(user: string | null): VirtualFileSystem {
    return Object.assign(Object.create(VirtualFileSystem.prototype) as VirtualFileSystem, { store: this.store, runAs: user, umask: 0o022 });
  }

  /** Where new modification times come from: the owning shell's clock (virtual when ShellOptions.startTime is set). */
  get clock(): () => number { return this.store.clock; }
  set clock(v: () => number) { this.store.clock = v; }

  /** Most bytes the files may hold in total; the agent's writes past it fail with "No space left on device". */
  get capacity(): number { return this.store.capacity; }
  set capacity(v: number) { this.store.capacity = v; }
  /** Bytes all files hold now (what `df` reports as used). */
  get usedBytes(): number { return this.store.usedBytes; }
  set usedBytes(v: number) { this.store.usedBytes = v; }
  private get root(): Dir { return this.store.root; }

  private get user(): string | null { const u = this.runAs; return u === 'root' ? null : u; }
  private get restricted(): boolean { return this.user !== null; }

  private reserve(delta: number, path: string) {
    if (delta > 0 && this.runAs !== null && this.usedBytes + delta > this.capacity) throw new ShellError(`${path}: ${NO_SPACE}`);
    this.usedBytes += delta;
  }

  /** Resolves `path` against `cwd` and collapses "." and "..". */
  static normalize(cwd: string, path: string): string {
    const full = path.startsWith('/') ? path : cwd.replace(/\/+$/, '') + '/' + path;
    const stack: string[] = [];
    for (const seg of full.split('/')) {
      if (seg === '' || seg === '.') continue;
      if (seg === '..') { stack.pop(); continue; }
      stack.push(seg);
    }
    return '/' + stack.join('/');
  }
  static getName(path: string): string {
    const p = VirtualFileSystem.normalize('/', path);
    return p === '/' ? '/' : p.slice(p.lastIndexOf('/') + 1);
  }
  static getParent(path: string): string {
    const p = VirtualFileSystem.normalize('/', path);
    const i = p.lastIndexOf('/');
    return i > 0 ? p.slice(0, i) : '/';
  }
  /** "drwxr-xr-x" style rendering of a mode; given a FileStat, "l" for a symbolic link. */
  static formatMode(st: FileStat): string;
  static formatMode(isDirectory: boolean, mode: number): string;
  static formatMode(a: boolean | FileStat, m?: number): string {
    const [isDirectory, mode, link] = typeof a === 'boolean' ? [a, m!, false] : [a.isDirectory, a.mode, a.isSymlink];
    const rwx = 'rwxrwxrwx';
    let s = link ? 'l' : isDirectory ? 'd' : '-';
    for (let i = 0; i < 9; i++) s += mode & (1 << (8 - i)) ? rwx[i] : '-';
    if (mode & 0o4000) s = s.slice(0, 3) + (s[3] === 'x' ? 's' : 'S') + s.slice(4);
    if (mode & 0o2000) s = s.slice(0, 6) + (s[6] === 'x' ? 's' : 'S') + s.slice(7);
    if (mode & 0o1000) s = s.slice(0, 9) + (s[9] === 'x' ? 't' : 'T');
    return s;
  }

  /**
   * Walks `path`; like Linux, each directory passed through needs search (x) permission, symbolic links in the middle are
   * followed (a relative one from the directory holding it), and the last one only when `follow`. ".." goes up from where
   * the walk really is. A loop fails after 40 links, never hangs. `why` says why `node` is null: "Permission denied",
   * "Not a directory" (a file mid-path), "Too many levels of symbolic links" or "No such file or directory".
   */
  private walk(path: string, follow = true): Spot {
    const user = this.user;
    const chain: { name: string; dir: Dir }[] = [{ name: '', dir: this.root }]; // the directories walked into, for ".." and the real path
    const todo: string[] = []; // the segments still to walk, the next one last
    const push = (p: string) => { const segs = p.split('/'); for (let i = segs.length - 1; i >= 0; i--) todo.push(segs[i]); };
    push(path);
    const real = (name: string) => chain.slice(1).map(c => '/' + c.name).join('') + '/' + name;
    const fail = (why: string): Spot => ({ parent: null, name: '', node: null, real: '', why });
    let hops = 0;
    let onFile = false; // a file was reached with more of the path to go
    while (todo.length > 0) {
      const seg = todo.pop()!;
      if (onFile) return fail(ERRNO.ENOTDIR);
      if (seg === '' || seg === '.') continue;
      const dir = chain[chain.length - 1].dir;
      if (!permits(dir, user, 1)) return fail(ERRNO.EACCES);
      if (seg === '..') { if (chain.length > 1) chain.pop(); continue; }
      const final = todo.every(t => t === '' || t === '.'), bare = todo.length === 0; // bare: no trailing slash, which would mean a directory
      const next = dir.children.get(seg);
      if (!next) return final ? { parent: bare ? dir : null, name: seg, node: null, real: real(seg), why: NOT_FOUND } : fail(NOT_FOUND);
      if (next instanceof LinkNode && (!bare || follow)) {
        if (++hops > MAX_LINK_HOPS) return fail(LOOP);
        if (next.target.startsWith('/')) chain.length = 1;
        push(next.target);
        continue;
      }
      if (bare) return { parent: dir, name: seg, node: next, real: real(seg), why: '' };
      if (next instanceof Dir) chain.push({ name: seg, dir: next }); else onFile = true;
    }
    if (chain.length === 1) return { parent: null, name: '/', node: this.root, real: '/', why: '' };
    const last = chain[chain.length - 1];
    return { parent: chain[chain.length - 2].dir, name: last.name, node: last.dir, real: real('').slice(0, -1), why: '' };
  }

  private find(path: string, follow = true): { node: Node | null; why: string } {
    const s = this.walk(path, follow);
    return { node: s.node, why: s.why === '' ? NOT_FOUND : s.why };
  }
  private node(path: string, follow = true): Node | null { return this.walk(path, follow).node; }

  /** Why `path` can't be reached: "Permission denied", "Not a directory", "Too many levels of symbolic links" or "No such file or directory". */
  whyMissing(path: string, follow = true): string { const f = this.find(path, follow); return f.node === null ? f.why : NOT_FOUND; }

  /** Where an entry is created, removed or renamed: its directory (which needs search permission) and name. The last name
   * is not followed, so this is the link itself when it is one. */
  private place(path: string): Spot & { parent: Dir } {
    const s = this.walk(path, false);
    if (s.parent === null) throw new ShellError(`${path}: ${s.node ? 'Device or resource busy' : s.why}`);
    return s as Spot & { parent: Dir };
  }
  private parentDir(path: string): Dir { return this.place(path).parent; }
  private allowed(n: Node, bits: number) { return permits(n, this.user, bits); }
  private require(n: Node, bits: number, path: string) {
    if (!this.allowed(n, bits)) throw new ShellError(`${path}: ${ERRNO.EACCES}`);
  }
  /** In a sticky directory (/tmp) only the entry's owner or the directory's may remove or rename the entry. */
  private requireNotSticky(dir: Dir, entry: Node, path: string) {
    const u = this.user;
    if (u !== null && dir.mode & 0o1000 && entry.owner !== u && dir.owner !== u) throw new ShellError(`${path}: ${ERRNO.EPERM}`);
  }

  /** @param follow false: a symbolic link counts itself (lstat), even one whose target is missing. */
  exists(path: string, follow = true): boolean { return this.node(path, follow) !== null; }
  isDirectory(path: string, follow = true): boolean { return this.node(path, follow) instanceof Dir; }
  isFile(path: string, follow = true): boolean { return this.node(path, follow) instanceof FileNode; }
  isSymlink(path: string): boolean { return this.node(path, false) instanceof LinkNode; }
  canRead(path: string): boolean { const n = this.node(path); return n !== null && this.allowed(n, 4); }
  canWrite(path: string): boolean { const n = this.node(path); return n !== null && this.allowed(n, 2); }
  canExecute(path: string): boolean { const n = this.node(path); return n !== null && this.allowed(n, 1); }

  /** Whether two paths lead to the same file or directory (`test -ef`): through hard or symbolic links. */
  sameFile(a: string, b: string, follow = true): boolean { const n = this.node(a, follow); return n !== null && n === this.node(b, follow); }

  /** @param follow false: a symbolic link's own status (lstat), with `linkTarget` set. */
  stat(path: string, follow = true): FileStat {
    const { node, why } = this.find(path, follow);
    if (node instanceof Dir) {
      const links = 2 + [...node.children.values()].filter(c => c instanceof Dir).length;
      return { isDirectory: true, size: 4096, modified: node.modified, mode: node.mode, owner: node.owner, links, blocks: 8, linkTarget: null, isSymlink: false };
    }
    if (node instanceof FileNode) {
      const size = node.reportedSize ?? node.size;
      return { isDirectory: false, size, modified: node.modified, mode: node.mode, owner: node.owner, links: node.nlink, blocks: Math.floor((size + 4095) / 4096) * 8, linkTarget: null, isSymlink: false };
    }
    if (node instanceof LinkNode) // a short link lives in its inode: no blocks
      return { isDirectory: false, size: utf8Length(node.target), modified: node.modified, mode: node.mode, owner: node.owner, links: node.nlink, blocks: 0, linkTarget: node.target, isSymlink: true };
    throw new ShellError(`${path}: ${why}`);
  }

  /** The path a symbolic link holds (`readlink`); "Invalid argument" for anything else. */
  readLink(path: string): string {
    const { node, why } = this.find(path, false);
    if (node instanceof LinkNode) return node.target;
    throw new ShellError(`${path}: ${node === null ? why : ERRNO.EINVAL}`);
  }

  /** The physical absolute path with every symbolic link resolved (`realpath`, `pwd -P`). With `existing` false the last
   * name may be missing (`readlink -f`). Null when it can't be resolved (`whyMissing` says why). */
  realPath(path: string, existing = true): string | null {
    const s = this.walk(path);
    return s.node !== null || (!existing && s.parent !== null) ? s.real : null;
  }

  /** Like `normalize`, but ".." leaves where the path so far really is, after its symbolic links, as the kernel resolves it
   * (the shell's cwd itself stays logical, as bash keeps it). */
  normalizePhysical(cwd: string, path: string): string {
    const full = path.startsWith('/') ? path : cwd.replace(/\/+$/, '') + '/' + path;
    // "." or ".." last names a directory itself, never a link to it (a logical cwd may be reached through one)
    if (['.', '..'].includes(path.split('/').filter(Boolean).pop() ?? '.')) return this.realPath(full) ?? VirtualFileSystem.normalize('/', full);
    if (!full.split('/').includes('..')) return VirtualFileSystem.normalize('/', full);
    let p = '/';
    for (const seg of full.split('/')) {
      if (seg === '' || seg === '.') continue;
      p = seg === '..' ? VirtualFileSystem.getParent(this.realPath(p) ?? p) : p === '/' ? '/' + seg : p + '/' + seg;
    }
    return p;
  }

  /** Creates a symbolic link at `path` holding `target` (`ln -s`), which need not exist. */
  createSymlink(path: string, target: string) {
    if (target.length === 0) throw new ShellError(`${path}: ${NOT_FOUND}`);
    const spot = this.place(path);
    if (spot.node) throw new ShellError(`${path}: ${ERRNO.EEXIST}`);
    this.require(spot.parent, 2, path);
    spot.parent.children.set(spot.name, Object.assign(new LinkNode(), { target, owner: this.runAs ?? spot.parent.owner, mode: 0o777, modified: this.store.clock() }));
    spot.parent.modified = this.store.clock();
  }

  /** Gives `existing` a second name, `path` (`ln`). With `follow` false a symbolic link is linked itself. Like Linux with
   * protected_hardlinks, the agent may only link files it owns or can read and write. */
  createHardLink(existing: string, path: string, follow = false) {
    const { node, why } = this.find(existing, follow);
    if (!node) throw new ShellError(`${existing}: ${why}`);
    if (node instanceof Dir) throw new ShellError(`${existing}: hard link not allowed for directory`);
    const spot = this.place(path);
    if (spot.node) throw new ShellError(`${path}: ${ERRNO.EEXIST}`);
    this.require(spot.parent, 2, path);
    if (this.restricted && node.owner !== this.runAs && !this.allowed(node, 6)) throw new ShellError(`${path}: ${ERRNO.EPERM}`);
    node.nlink++;
    spot.parent.children.set(spot.name, node);
    spot.parent.modified = this.store.clock();
  }

  readText(path: string): string { const f = this.readNode(path); return f.data === null ? f.content : bytesToText(f.data); }
  /** What `cat` and `<` put on a pipe: like readText, but a binary file's bytes survive (see bytes.ts). */
  readPipeText(path: string): string { const f = this.readNode(path); return f.data === null ? f.content : decodeBytes(f.data); }
  /** The file's exact bytes (UTF-8 for files written as text). */
  readBytes(path: string): Uint8Array { const f = this.readNode(path); return f.data !== null ? f.data.slice() : f.raw; }

  private readNode(path: string): FileNode {
    const { node, why } = this.find(path);
    if (node instanceof FileNode) { this.require(node, 4, path); return node; }
    if (node instanceof Dir) throw new ShellError(`${path}: ${ERRNO.EISDIR}`);
    throw new ShellError(`${path}: ${why}`);
  }

  writeText(path: string, content: string) { this.write(path, content, null, false); }
  appendText(path: string, content: string) { this.write(path, content, null, true); }
  writeBytes(path: string, data: Uint8Array) { this.write(path, null, data.slice(), false); }

  private write(path: string, content: string | null, data: Uint8Array | null, append: boolean) {
    path = VirtualFileSystem.normalize('/', path);
    if (path === '/dev/null' && this.node(path) !== null) return;
    const spot = this.walk(path); // through a symbolic link, even a dangling one: its target is written or created
    const dir = spot.parent;
    if (dir === null) throw new ShellError(`${path}: ${spot.node ? ERRNO.EISDIR : spot.why}`);
    const existing = spot.node;
    if (existing) {
      if (!(existing instanceof FileNode)) throw new ShellError(`${path}: ${ERRNO.EISDIR}`);
      this.require(existing, 2, path);
      existing.store(content, data, append, d => this.reserve(d, path));
      existing.modified = this.store.clock();
    } else {
      this.require(dir, 2, path);
      const file = Object.assign(new FileNode(), { modified: this.store.clock(), owner: this.runAs ?? dir.owner, mode: 0o666 & ~this.umask });
      file.store(content, data, false, d => this.reserve(d, path));
      dir.children.set(spot.name, file);
    }
    dir.modified = this.store.clock();
  }

  /** Back-dates a path (and optionally everything under it). Unchecked: for setting up the machine. */
  setModified(path: string, when: number, recursive = false) {
    const node = this.node(path);
    if (!node) return;
    const walk = (n: Node) => {
      n.modified = when;
      if (recursive && n instanceof Dir) for (const c of n.children.values()) walk(c);
    };
    walk(node);
  }

  /** Sets a modification time (touch -d, tar and unzip restoring one); as with utime(2), only the owner may. */
  setModifiedTime(path: string, when: number, follow = true) {
    const { node, why } = this.find(path, follow);
    if (!node) throw new ShellError(`${path}: ${why}`);
    if (this.restricted && node.owner !== this.runAs) throw new ShellError(`${path}: ${ERRNO.EPERM}`);
    node.modified = when;
  }

  /** @internal Every file, directory and symbolic link, parents first (contents shared, never copied: they are not changed in place). */
  *entries(): Generator<VfsEntry> {
    const first = new Map<Node, string>(); // where each hard-linked node was seen first
    const walk = function* (n: Node, path: string): Generator<VfsEntry> {
      let hardLink: string | null = null;
      if (n.nlink > 1) { hardLink = first.get(n) ?? null; if (hardLink === null) first.set(n, path); }
      yield entryOf(n, path, hardLink);
      if (n instanceof Dir) for (const [name, child] of sortedChildren(n)) yield* walk(child, childPath(path, name));
    };
    yield* walk(this.root, '/');
  }

  /**
   * @internal A function that lists the paths whose entry (as `entries()` gives it) changed since it last ran, sorted: the
   * same list as comparing two snapshots, without copying the tree. Each run walks the nodes but builds entries only for
   * nodes changed since, names a directory gained or replaced, and hard-linked files, whose first name can move when
   * another name changes.
   */
  trackChanges(): () => string[] {
    let seen = Infinity; // the first run sees everything as new
    const known = new Map<string, { entry: VfsEntry; node: Node }>();
    const names = new Map<string, [string, Node][]>(); // each directory's names when last looked at
    const run = (): string[] => {
      const now = changeClock;
      const changed: string[] = [];
      const gone = new Set<string>(); // what was below a removed or replaced name, until seen again
      const first = new Map<Node, string>();
      const markGone = (path: string) => {
        gone.add(path);
        for (const [name] of names.get(path) ?? []) markGone(childPath(path, name));
      };
      const visit = (n: Node, path: string, moved: boolean) => {
        let hard: string | null = null;
        if (n.nlink > 1) { hard = first.get(n) ?? null; if (hard === null) first.set(n, path); }
        if (moved || n.changed > seen || n.nlink > 1) {
          const e = entryOf(n, path, hard), old = known.get(path);
          if (!old || !sameEntry(old.entry, e)) changed.push(path);
          known.set(path, { entry: e, node: n });
          gone.delete(path);
        }
        if (!(n instanceof Dir)) { if (moved) names.delete(path); return; } // it may have been a directory
        if (!moved && n.listed <= seen) {
          for (const [name, child] of names.get(path)!) // the same names as last time
            if (child instanceof Dir || child.changed > seen || child.nlink > 1) visit(child, childPath(path, name), false);
          return;
        }
        // names gained, lost or replaced: merge the sorted lists of before and now
        const before = names.get(path) ?? [], list = sortedChildren(n);
        let i = 0;
        for (const [name, child] of list) {
          while (i < before.length && ordinal(before[i][0], name) < 0) markGone(childPath(path, before[i++][0]));
          const had = i < before.length && before[i][0] === name, same = had && before[i][1] === child;
          if (had) i++;
          if (!(moved || !same || child instanceof Dir || child.changed > seen || child.nlink > 1)) continue;
          const p = childPath(path, name);
          if (had && (moved || !same)) markGone(p); // replaced: what was below it is gone unless seen again
          visit(child, p, moved || !same);
        }
        while (i < before.length) markGone(childPath(path, before[i++][0]));
        names.set(path, list);
      };
      const root = this.root;
      visit(root, '/', seen === Infinity || known.get('/')!.node !== root);
      for (const p of gone) { known.delete(p); names.delete(p); changed.push(p); }
      seen = now;
      return changed.sort(ordinal);
    };
    run();
    return run;
  }

  /** @internal Replaces every file with `entries` (parents first, as `entries()` gives them): a snapshot restore. */
  replaceAll(entries: Iterable<VfsEntry>) {
    const root = Object.assign(new Dir(), { mode: DEFAULT_DIR_MODE });
    let used = 0;
    // by name only, never through a link: an entry can't land anywhere but its own path
    const plain = (path: string) => {
      let cur: Node | undefined = root;
      for (const seg of path.split('/')) if (seg !== '') cur = cur instanceof Dir ? cur.children.get(seg) : undefined;
      return cur;
    };
    for (const e of entries) {
      const parts = e.path.split('/').slice(1);
      const dir = plain(VirtualFileSystem.getParent(e.path));
      if (e.hardLink !== null) {
        const same = plain(e.hardLink);
        if (!same || same instanceof Dir) throw new ShellError(`${e.path}: hard link to ${e.hardLink}, which is not an earlier file`);
        if (!(dir instanceof Dir)) throw new ShellError(`${e.path}: parent is not a directory`);
        if (dir.children.has(parts[parts.length - 1])) throw new ShellError(`${e.path}: ${ERRNO.EEXIST}`);
        same.nlink++;
        dir.children.set(parts[parts.length - 1], same);
        continue;
      }
      let node: Node;
      if (e.isDirectory) node = e.path === '/' ? root : new Dir();
      else if (e.linkTarget !== null) node = Object.assign(new LinkNode(), { target: e.linkTarget });
      else {
        const f = new FileNode();
        f.store(e.text ?? (e.data ? null : ''), e.data ? e.data.slice() : null, false, d => { used += d; });
        f.reportedSize = e.reportedSize;
        node = f;
      }
      Object.assign(node, { mode: e.mode, owner: e.owner, modified: e.modified });
      if (e.path === '/') continue;
      if (!(dir instanceof Dir)) throw new ShellError(`${e.path}: parent is not a directory`);
      dir.children.set(parts[parts.length - 1], node);
    }
    this.store.root = root;
    this.store.usedBytes = used;
  }

  setReportedSize(path: string, size: number) {
    const n = this.node(path);
    if (n instanceof FileNode) n.reportedSize = size;
  }

  touch(path: string) {
    const { node, why } = this.find(path);
    if (node) { this.require(node, 2, path); node.modified = this.store.clock(); }
    else if (why === ERRNO.EACCES) throw new ShellError(`${path}: ${ERRNO.EACCES}`);
    else this.writeText(path, '');
  }

  /** `parents`: make missing parents too, each one also writable and searchable by its owner (mkdir -p). */
  mkdir(path: string, parents = false, intermediate = false) {
    path = VirtualFileSystem.normalize('/', path);
    if (this.node(path, false)) { // a symbolic link counts, even a dangling one
      if (parents && this.isDirectory(path)) return;
      throw new ShellError(`cannot create directory '${path}': ${ERRNO.EEXIST}`);
    }
    const parent = VirtualFileSystem.getParent(path);
    if (parents && !this.exists(parent)) this.mkdir(parent, true, true);
    const spot = this.place(path);
    this.require(spot.parent, 2, path);
    spot.parent.children.set(spot.name, Object.assign(new Dir(), { modified: this.store.clock(), owner: this.runAs ?? spot.parent.owner, mode: (0o777 & ~this.umask) | (intermediate ? 0o300 : 0) }));
    spot.parent.modified = this.store.clock();
  }

  /** Entry names in a directory, sorted by code unit (like `LC_ALL=C ls`). */
  list(path: string): string[] {
    const { node, why } = this.find(path);
    if (node instanceof Dir) { this.require(node, 4, path); return [...node.children.keys()].sort(ordinal); }
    throw new ShellError(`${path}: ${node === null ? why : ERRNO.ENOTDIR}`);
  }

  /** Removes an entry (a symbolic link itself, never what it leads to); `recursive` for a directory with entries, whose
   * links are removed, not followed. */
  delete(path: string, recursive = false) {
    path = VirtualFileSystem.normalize('/', path);
    if (path === '/') throw new ShellError("it is dangerous to operate recursively on '/'");
    const { node, why } = this.find(path, false);
    if (!node) throw new ShellError(`${path}: ${why}`);
    if (node instanceof Dir && node.children.size > 0 && !recursive) throw new ShellError(`${path}: ${ERRNO.ENOTEMPTY}`);
    const { parent, name } = this.place(path);
    if (node instanceof Dir) this.requireTreeRemovable(node, path);
    this.require(parent, 2, path);
    this.requireNotSticky(parent, node, path);
    parent.children.delete(name);
    this.usedBytes -= unlink(node);
  }

  // shortcut: all or nothing, naming the first entry that can't go; real rm -r removes what it can.
  private requireTreeRemovable(d: Dir, path: string) {
    if (d.children.size === 0) return;
    const first = [...d.children.keys()].sort(ordinal)[0];
    this.require(d, 4, path);
    this.require(d, 3, path + '/' + first);
    for (const [name, child] of d.children) {
      this.requireNotSticky(d, child, path + '/' + name);
      if (child instanceof Dir) this.requireTreeRemovable(child, path + '/' + name);
    }
  }

  /**
   * Copies a file or tree, like `cp -r`: entries the user can't read are skipped and passed to `denied` (default: fail the
   * whole copy). `followTop`: copy what `source` leads to when it is a symbolic link (cp without -r, or -H), rather than the
   * link. `followAll`: copy what every symbolic link in the tree leads to (cp -L); a link back into a directory being copied
   * goes to `broken` as "cyclic", one leading nowhere with its reason (default: fail the whole copy). `tick` is called for
   * each entry copied: the caller's time limit (links can make a copy far bigger than the tree).
   */
  copy(source: string, dest: string, denied: (path: string, isDir: boolean) => void = p => { throw new ShellError(`${p}: ${ERRNO.EACCES}`); },
    followTop = true, followAll = false, broken: (path: string, reason: string) => void = (p, reason) => { throw new ShellError(`${p}: ${reason}`); },
    tick?: () => void) {
    source = VirtualFileSystem.normalize('/', source);
    const { node, why } = this.find(source, followTop || followAll);
    if (!node) throw new ShellError(`${source}: ${why}`);
    dest = VirtualFileSystem.normalize('/', dest);
    const place = this.place(dest);
    if (node instanceof Dir && (place.real + '/').startsWith(this.realPath(source) + '/'))
      throw new ShellError(`cannot copy a directory, '${source}', into itself, '${dest}'`);
    const target = place.node instanceof LinkNode ? this.node(dest) : place.node; // a file is copied through a link onto its target
    if (target instanceof Dir && !(node instanceof Dir)) throw new ShellError(`${dest}: ${ERRNO.EISDIR}`);
    const parent = place.parent;
    if (target) this.require(target, 2, dest); else this.require(parent, 2, dest);
    const user = this.user;
    const owner = this.runAs ?? parent.owner;
    const copying = new Set<Dir>(); // directories on the way down, so -L can't loop
    let cloned = 0;
    const clone = (n: Node, path: string): Node | null => {
      if (n instanceof LinkNode) {
        if (!followAll) return Object.assign(new LinkNode(), { target: n.target, owner, mode: n.mode, modified: this.store.clock() });
        const { node: to, why: reason } = this.find(path);
        if (!to) { broken(path, reason); return null; }
        n = to;
      }
      // a directory already being copied further up, reached through a link (even one higher up), as cp -L reports it
      if (n instanceof Dir && copying.has(n)) { broken(path, 'cyclic'); return null; }
      tick?.();
      if (!permits(n, user, n instanceof Dir ? 5 : 4)) { denied(path, n instanceof Dir); return null; }
      if (n instanceof FileNode) {
        // the copy is built before it is stored: links can make it far bigger than the tree, so charge it as it grows
        if (this.runAs !== null && this.usedBytes + (cloned += n.size) > this.capacity) throw new ShellError(`${dest}: ${NO_SPACE}`);
        // the agent's copy loses setuid/setgid and takes its umask (cp without -p); the host's keeps the mode
        const f = Object.assign(new FileNode(), { owner, mode: this.runAs === null ? n.mode : n.mode & 0o777 & ~this.umask, size: n.size, data: n.data, modified: this.store.clock() });
        f.content = n.content;
        return f;
      }
      const dir = n as Dir;
      const copy = Object.assign(new Dir(), { owner, mode: this.runAs === null ? n.mode : n.mode & ~this.umask, modified: this.store.clock() });
      copying.add(dir);
      for (const [k, v] of dir.children) { const c = clone(v, path + '/' + k); if (c) copy.children.set(k, c); }
      copying.delete(dir);
      return copy;
    };
    const c = clone(node, source);
    if (!c) return;
    // copying onto an existing file writes into it, keeping its owner and mode, as cp does
    if (target instanceof FileNode && c instanceof FileNode) {
      target.store(c.data ? null : c.content, c.data, false, d => this.reserve(d, dest));
      target.modified = this.store.clock();
    } else {
      const old = place.node;
      const oldSize = old ? treeSize(old) : 0;
      this.reserve(treeSize(c) - oldSize, dest);
      if (old) this.usedBytes += oldSize - unlink(old);
      parent.children.set(place.name, c);
    }
  }

  /** Renames an entry; a symbolic link is moved itself, and a link at `dest` is replaced, not followed. */
  move(source: string, dest: string) {
    source = VirtualFileSystem.normalize('/', source);
    dest = VirtualFileSystem.normalize('/', dest);
    const { node, why } = this.find(source, false);
    if (!node) throw new ShellError(`${source}: ${why}`);
    const from = this.place(source);
    const to = this.place(dest);
    // by real paths: through a link, a directory could otherwise be moved into itself and cut off from the tree
    if ((to.real + '/').startsWith(from.real + '/')) throw new ShellError(`cannot move '${source}' to a subdirectory of itself, '${dest}'`);
    const existing = to.node;
    if (existing instanceof Dir) throw new ShellError(`${dest}: ${ERRNO.EISDIR}`);
    if (existing === node) return; // two hard links of one file: rename(2) leaves both
    const target = to.parent;
    this.require(from.parent, 2, source);
    this.require(target, 2, dest);
    if (node instanceof Dir && from.parent !== target) this.require(node, 2, source); // its ".." entry changes
    this.requireNotSticky(from.parent, node, source);
    if (existing) this.requireNotSticky(target, existing, dest);
    from.parent.children.delete(from.name);
    if (existing) this.usedBytes -= unlink(existing);
    target.children.set(to.name, node);
  }

  /** Changes permission bits. While restricted, only the owner may. */
  chmod(path: string, mode: number) {
    const { node, why } = this.find(path);
    if (!node) throw new ShellError(`cannot access '${path}': ${why}`);
    if (this.restricted && node.owner !== this.runAs) throw new ShellError(`changing permissions of '${path}': ${ERRNO.EPERM}`);
    node.mode = mode & 0o7777;
  }

  /** Changes the owner (and, with `recursive`, everything below). Unrestricted callers only. */
  setOwner(path: string, owner: string, recursive = false) {
    const { node, why } = this.find(path);
    if (!node) throw new ShellError(`cannot access '${path}': ${why}`);
    if (this.restricted) throw new ShellError(`changing ownership of '${path}': ${ERRNO.EPERM}`);
    const apply = (n: Node) => {
      n.owner = owner;
      if (recursive && n instanceof Dir) for (const c of n.children.values()) apply(c);
    };
    apply(node);
  }
}

/** Where a path leads: the directory holding (or that would hold) its last entry, that entry's name and node (null if
 * absent), and its physical path. `parent` is null when the path can't be reached (`why` says why), and for "/". */
interface Spot { parent: Dir | null; name: string; node: Node | null; real: string; why: string }

/** Symbolic links followed in one lookup before it fails with "Too many levels of symbolic links", as on Linux. */
const MAX_LINK_HOPS = 40;
export const LOOP = ERRNO.ELOOP;

function permits(n: Node, user: string | null, bits: number) {
  return user === null || ((n.mode >> (n.owner === user ? 6 : 0)) & bits) === bits;
}
const NOT_FOUND = ERRNO.ENOENT;
function treeSize(n: Node): number {
  if (n instanceof FileNode) return n.size;
  let s = 0;
  if (n instanceof Dir) for (const c of n.children.values()) s += treeSize(c);
  return s;
}
/** Drops one name of each node in a tree being removed; returns the bytes freed (a file goes with its last name). */
function unlink(n: Node): number {
  if (n instanceof Dir) { let s = 0; for (const c of n.children.values()) s += unlink(c); return s; }
  return --n.nlink > 0 || !(n instanceof FileNode) ? 0 : n.size;
}
export function ordinal(a: string, b: string) { return a < b ? -1 : a > b ? 1 : 0; }

const sortedChildren = (d: Dir): [string, Node][] => [...d.children].sort((a, b) => ordinal(a[0], b[0]));
const childPath = (dir: string, name: string) => dir === '/' ? '/' + name : `${dir}/${name}`;

function entryOf(n: Node, path: string, hardLink: string | null): VfsEntry {
  const base = { path, mode: n.mode, owner: n.owner, modified: n.modified, hardLink, linkTarget: null, isDirectory: false, text: null, data: null, reportedSize: null };
  if (n instanceof FileNode) return { ...base, text: n.data === null ? n.content : null, data: n.data, reportedSize: n.reportedSize };
  if (n instanceof LinkNode) return { ...base, linkTarget: n.target };
  return { ...base, isDirectory: true };
}

/** @internal Whether two entries are the same (contents by value). */
export function sameEntry(o: VfsEntry, e: VfsEntry): boolean {
  return o.isDirectory === e.isDirectory && o.mode === e.mode && o.owner === e.owner && o.modified === e.modified && o.linkTarget === e.linkTarget &&
    o.hardLink === e.hardLink && o.reportedSize === e.reportedSize && o.text === e.text && sameBytes(o.data, e.data);
}

function sameBytes(x: Uint8Array | null, y: Uint8Array | null) {
  if (x === y) return true;
  if (!x || !y || x.length !== y.length) return false;
  for (let i = 0; i < x.length; i++) if (x[i] !== y[i]) return false;
  return true;
}
