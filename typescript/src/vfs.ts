import { decodeBytes, encodeText, hasEscapes, utf8Length, bytesToText } from './bytes.js';

/** Thrown by commands and the filesystem; becomes "cmd: message" on stderr with the given exit code. */
export class ShellError extends Error {
  constructor(message: string, public readonly exitCode = 1) { super(message); }
}

export interface FileStat {
  isDirectory: boolean;
  size: number;
  /** Modification time, ms since the epoch (UTC). */
  modified: number;
  /** Unix permission bits, e.g. 0o755 (plus 0o1000 for sticky). */
  mode: number;
  owner: string;
}

abstract class Node {
  modified = Date.now();
  owner = 'root';
  mode = 0;
}
class Dir extends Node { readonly children = new Map<string, Node>(); }
class FileNode extends Node {
  /** Text content (null when `data` holds the bytes, or while `chunks` holds pending appends). */
  private text: string | null = '';
  private chunks: string[] | null = null; // `>>` writes accumulate here, so append loops stay linear
  data: Uint8Array | null = null; // set for binary files; text is then unused
  reportedSize: number | null = null; // what `ls -l` shows for a program stub
  size = 0; // real bytes held, counted against the capacity

  get content(): string {
    if (this.text === null) { this.text = this.chunks!.join(''); this.chunks = null; }
    return this.text;
  }
  set content(v: string) { this.text = v; this.chunks = null; }
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
export const NO_SPACE = 'No space left on device';

/** The state every view of one filesystem shares. */
interface Store { root: Dir; capacity: number; usedBytes: number }

/**
 * In-memory Unix-style filesystem with owners and permission bits. Calls from your app are unrestricted (root).
 * While the shell runs the agent's commands it runs them as the agent (a view made by `as`), so the agent is an ordinary user:
 * it can't write to root-owned places such as /etc or /usr/bin. New files belong to the owner of their directory
 * (or to the agent, when it creates them). Binary files keep their exact bytes; the text API reads them as lossy UTF-8.
 * shortcut: no groups, no symlinks; add them if group permissions are needed.
 */
export class VirtualFileSystem {
  private readonly store: Store;
  /** The user this view acts as, or null for unrestricted access (your app). */
  readonly runAs: string | null = null;

  constructor() { this.store = { root: Object.assign(new Dir(), { mode: DEFAULT_DIR_MODE }), capacity: Number.MAX_SAFE_INTEGER, usedBytes: 0 }; }

  /**
   * A view of the same files that acts as `user` (null lifts the restrictions). The shell hands its commands such a
   * view of the agent, so whose call it is travels with the object instead of an ambient async context.
   */
  as(user: string | null): VirtualFileSystem {
    return Object.assign(Object.create(VirtualFileSystem.prototype) as VirtualFileSystem, { store: this.store, runAs: user });
  }

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
  /** "drwxr-xr-x" style rendering of a mode. */
  static formatMode(isDirectory: boolean, mode: number): string {
    const rwx = 'rwxrwxrwx';
    let s = isDirectory ? 'd' : '-';
    for (let i = 0; i < 9; i++) s += mode & (1 << (8 - i)) ? rwx[i] : '-';
    if (mode & 0o1000) s = s.slice(0, 9) + (s[9] === 'x' ? 't' : 'T');
    return s;
  }

  private find(path: string): { node: Node | null; denied: boolean } {
    const user = this.user;
    let cur: Node = this.root;
    for (const seg of VirtualFileSystem.normalize('/', path).split('/')) {
      if (seg === '') continue;
      if (!(cur instanceof Dir)) return { node: null, denied: false };
      if (!permits(cur, user, 1)) return { node: null, denied: true };
      const next = cur.children.get(seg);
      if (!next) return { node: null, denied: false };
      cur = next;
    }
    return { node: cur, denied: false };
  }
  private node(path: string): Node | null { return this.find(path).node; }

  /** Why `path` can't be reached: "Permission denied" or "No such file or directory". */
  whyMissing(path: string): string { const f = this.find(path); return missing(f.node === null && f.denied); }

  private parentDir(path: string): Dir {
    const { node, denied } = this.find(VirtualFileSystem.getParent(path));
    if (node instanceof Dir) { this.require(node, 1, path); return node; }
    throw new ShellError(`${path}: ${node === null ? missing(denied) : 'Not a directory'}`);
  }
  private allowed(n: Node, bits: number) { return permits(n, this.user, bits); }
  private require(n: Node, bits: number, path: string) {
    if (!this.allowed(n, bits)) throw new ShellError(`${path}: Permission denied`);
  }
  /** In a sticky directory (/tmp) only the entry's owner or the directory's may remove or rename the entry. */
  private requireNotSticky(dir: Dir, entry: Node, path: string) {
    const u = this.user;
    if (u !== null && dir.mode & 0o1000 && entry.owner !== u && dir.owner !== u) throw new ShellError(`${path}: Operation not permitted`);
  }

  exists(path: string): boolean { return this.node(path) !== null; }
  isDirectory(path: string): boolean { return this.node(path) instanceof Dir; }
  isFile(path: string): boolean { return this.node(path) instanceof FileNode; }
  canRead(path: string): boolean { const n = this.node(path); return n !== null && this.allowed(n, 4); }
  canWrite(path: string): boolean { const n = this.node(path); return n !== null && this.allowed(n, 2); }
  canExecute(path: string): boolean { const n = this.node(path); return n !== null && this.allowed(n, 1); }

  stat(path: string): FileStat {
    const { node, denied } = this.find(path);
    if (node instanceof Dir) return { isDirectory: true, size: 4096, modified: node.modified, mode: node.mode, owner: node.owner };
    if (node instanceof FileNode)
      return { isDirectory: false, size: node.reportedSize ?? node.size, modified: node.modified, mode: node.mode, owner: node.owner };
    throw new ShellError(`${path}: ${missing(denied)}`);
  }

  readText(path: string): string { const f = this.readNode(path); return f.data === null ? f.content : bytesToText(f.data); }
  /** What `cat` and `<` put on a pipe: like readText, but a binary file's bytes survive (see bytes.ts). */
  readPipeText(path: string): string { const f = this.readNode(path); return f.data === null ? f.content : decodeBytes(f.data); }
  /** The file's exact bytes (UTF-8 for files written as text). */
  readBytes(path: string): Uint8Array { const f = this.readNode(path); return f.data !== null ? f.data.slice() : f.raw; }

  private readNode(path: string): FileNode {
    const { node, denied } = this.find(path);
    if (node instanceof FileNode) { this.require(node, 4, path); return node; }
    if (node instanceof Dir) throw new ShellError(`${path}: Is a directory`);
    throw new ShellError(`${path}: ${missing(denied)}`);
  }

  writeText(path: string, content: string) { this.write(path, content, null, false); }
  appendText(path: string, content: string) { this.write(path, content, null, true); }
  writeBytes(path: string, data: Uint8Array) { this.write(path, null, data.slice(), false); }

  private write(path: string, content: string | null, data: Uint8Array | null, append: boolean) {
    path = VirtualFileSystem.normalize('/', path);
    if (path === '/dev/null' && this.node(path) !== null) return;
    const dir = this.parentDir(path);
    const name = VirtualFileSystem.getName(path);
    const existing = dir.children.get(name);
    if (existing) {
      if (!(existing instanceof FileNode)) throw new ShellError(`${path}: Is a directory`);
      this.require(existing, 2, path);
      existing.store(content, data, append, d => this.reserve(d, path));
      existing.modified = Date.now();
    } else {
      this.require(dir, 2, path);
      const file = Object.assign(new FileNode(), { owner: this.runAs ?? dir.owner, mode: DEFAULT_FILE_MODE });
      file.store(content, data, false, d => this.reserve(d, path));
      dir.children.set(name, file);
    }
    dir.modified = Date.now();
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
  setModifiedTime(path: string, when: number) {
    const { node, denied } = this.find(path);
    if (!node) throw new ShellError(`${path}: ${missing(denied)}`);
    if (this.restricted && node.owner !== this.runAs) throw new ShellError(`${path}: Operation not permitted`);
    node.modified = when;
  }

  setReportedSize(path: string, size: number) {
    const n = this.node(path);
    if (n instanceof FileNode) n.reportedSize = size;
  }

  touch(path: string) {
    const { node, denied } = this.find(path);
    if (node) { this.require(node, 2, path); node.modified = Date.now(); }
    else if (denied) throw new ShellError(`${path}: Permission denied`);
    else this.writeText(path, '');
  }

  mkdir(path: string, parents = false) {
    path = VirtualFileSystem.normalize('/', path);
    const existing = this.node(path);
    if (existing) {
      if (parents && existing instanceof Dir) return;
      throw new ShellError(`cannot create directory '${path}': File exists`);
    }
    const parent = VirtualFileSystem.getParent(path);
    if (parents && !this.exists(parent)) this.mkdir(parent, true);
    const dir = this.parentDir(path);
    this.require(dir, 2, path);
    dir.children.set(VirtualFileSystem.getName(path), Object.assign(new Dir(), { owner: this.runAs ?? dir.owner, mode: DEFAULT_DIR_MODE }));
    dir.modified = Date.now();
  }

  /** Entry names in a directory, sorted by code unit (like `LC_ALL=C ls`). */
  list(path: string): string[] {
    const { node, denied } = this.find(path);
    if (node instanceof Dir) { this.require(node, 4, path); return [...node.children.keys()].sort(ordinal); }
    if (node instanceof FileNode) throw new ShellError(`${path}: Not a directory`);
    throw new ShellError(`${path}: ${missing(denied)}`);
  }

  delete(path: string, recursive = false) {
    path = VirtualFileSystem.normalize('/', path);
    if (path === '/') throw new ShellError("it is dangerous to operate recursively on '/'");
    const { node, denied } = this.find(path);
    if (!node) throw new ShellError(`${path}: ${missing(denied)}`);
    if (node instanceof Dir && node.children.size > 0 && !recursive) throw new ShellError(`${path}: Directory not empty`);
    const parent = this.parentDir(path);
    if (node instanceof Dir) this.requireTreeRemovable(node, path);
    this.require(parent, 2, path);
    this.requireNotSticky(parent, node, path);
    parent.children.delete(VirtualFileSystem.getName(path));
    this.usedBytes -= treeSize(node);
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

  /** Copies a file or tree, like `cp -r`: entries the user can't read are skipped and passed to `denied`
   * (default: fail the whole copy). */
  copy(source: string, dest: string, denied: (path: string, isDir: boolean) => void = p => { throw new ShellError(`${p}: Permission denied`); }) {
    source = VirtualFileSystem.normalize('/', source);
    const { node, denied: blocked } = this.find(source);
    if (!node) throw new ShellError(`${source}: ${missing(blocked)}`);
    dest = VirtualFileSystem.normalize('/', dest);
    if (node instanceof Dir && (dest + '/').startsWith(source + '/'))
      throw new ShellError(`cannot copy a directory, '${source}', into itself, '${dest}'`);
    const target = this.node(dest);
    if (target instanceof Dir && node instanceof FileNode) throw new ShellError(`${dest}: Is a directory`);
    const parent = this.parentDir(dest);
    if (target) this.require(target, 2, dest); else this.require(parent, 2, dest);
    const user = this.user;
    const owner = this.runAs ?? parent.owner;
    const clone = (n: Node, path: string): Node | null => {
      if (!permits(n, user, n instanceof Dir ? 5 : 4)) { denied(path, n instanceof Dir); return null; }
      if (n instanceof FileNode) {
        const f = Object.assign(new FileNode(), { owner, mode: n.mode, size: n.size, data: n.data });
        f.content = n.content;
        return f;
      }
      const copy = Object.assign(new Dir(), { owner, mode: n.mode });
      for (const [k, v] of (n as Dir).children) { const c = clone(v, path + '/' + k); if (c) copy.children.set(k, c); }
      return copy;
    };
    const c = clone(node, source);
    if (!c) return;
    // copying onto an existing file writes into it, keeping its owner and mode, as cp does
    if (target instanceof FileNode && c instanceof FileNode) {
      target.store(c.data ? null : c.content, c.data, false, d => this.reserve(d, dest));
      target.modified = Date.now();
    } else {
      this.reserve(treeSize(c) - (target ? treeSize(target) : 0), dest);
      parent.children.set(VirtualFileSystem.getName(dest), c);
    }
  }

  move(source: string, dest: string) {
    source = VirtualFileSystem.normalize('/', source);
    dest = VirtualFileSystem.normalize('/', dest);
    const { node, denied } = this.find(source);
    if (!node) throw new ShellError(`${source}: ${missing(denied)}`);
    if ((dest + '/').startsWith(source + '/')) throw new ShellError(`cannot move '${source}' to a subdirectory of itself, '${dest}'`);
    const existing = this.node(dest);
    if (existing instanceof Dir) throw new ShellError(`${dest}: Is a directory`);
    const target = this.parentDir(dest);
    const from = this.parentDir(source);
    this.require(from, 2, source);
    this.require(target, 2, dest);
    if (node instanceof Dir && from !== target) this.require(node, 2, source); // its ".." entry changes
    this.requireNotSticky(from, node, source);
    if (existing) this.requireNotSticky(target, existing, dest);
    from.children.delete(VirtualFileSystem.getName(source));
    if (existing) this.usedBytes -= treeSize(existing);
    target.children.set(VirtualFileSystem.getName(dest), node);
  }

  /** Changes permission bits. While restricted, only the owner may. */
  chmod(path: string, mode: number) {
    const { node, denied } = this.find(path);
    if (!node) throw new ShellError(`cannot access '${path}': ${missing(denied)}`);
    if (this.restricted && node.owner !== this.runAs) throw new ShellError(`changing permissions of '${path}': Operation not permitted`);
    node.mode = mode & 0o7777;
  }

  /** Changes the owner (and, with `recursive`, everything below). Unrestricted callers only. */
  setOwner(path: string, owner: string, recursive = false) {
    const { node, denied } = this.find(path);
    if (!node) throw new ShellError(`cannot access '${path}': ${missing(denied)}`);
    if (this.restricted) throw new ShellError(`changing ownership of '${path}': Operation not permitted`);
    const apply = (n: Node) => {
      n.owner = owner;
      if (recursive && n instanceof Dir) for (const c of n.children.values()) apply(c);
    };
    apply(node);
  }
}

function permits(n: Node, user: string | null, bits: number) {
  return user === null || ((n.mode >> (n.owner === user ? 6 : 0)) & bits) === bits;
}
function missing(denied: boolean) { return denied ? 'Permission denied' : 'No such file or directory'; }
function treeSize(n: Node): number {
  if (n instanceof FileNode) return n.size;
  let s = 0;
  for (const c of (n as Dir).children.values()) s += treeSize(c);
  return s;
}
export function ordinal(a: string, b: string) { return a < b ? -1 : a > b ? 1 : 0; }
