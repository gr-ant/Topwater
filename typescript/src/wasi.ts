// WASI preview1 over the shell's virtual filesystem, for the python3 command (commands/python.ts).
//
// The guest is bashkit's CPython build (assets/python-wasm). It reaches only what this module hands it: the virtual
// filesystem as the agent's user, the command's stdin and output, a clock, random bytes, and HTTP through a callback the
// command supplies. There is no host filesystem, socket, process or thread API: sockets answer ENOTSUP.
//
// JavaScript cannot interrupt WebAssembly on the thread that runs it, so the guest runs in a Worker (worker_threads in
// Node, a Web Worker in browsers) and this thread only serves it: each WASI call is posted here and the worker waits
// (Atomics.wait) until the answer is in a SharedArrayBuffer. The guest's memory is made shared (and given the call's
// maximum) before compiling, so the functions below read and write it directly. This thread never waits on the guest:
// at the deadline it terminates the worker. See docs/python.md and the porting contract for the other ports.
import { ShellError, VirtualFileSystem } from './vfs.js';
import { decodeBytes, encodeText } from './bytes.js';
import { ERRNO } from './spec.generated.js';

// ---------- artifacts and runtime ----------

/** sha256 of the interpreter: the module python.wasm.gz decompresses to (the same module as python.wasm.xz in
 * assets/python-wasm/MANIFEST, recompressed by tools/python_wasm_gz.py), and the standard library. */
export const ARTIFACT_SHA256: Readonly<Record<string, string>> = {
  'python.wasm': '71df1f2cf7b3dbc0ee56b7fc58243f6ae17d9d57cdbb50855598241a7a357600',
  'python314.zip': 'a2fbfc15b11fe851fffe3a84369f4d8c73992904ea23bbc045d89373737e15ea',
};
export const STDLIB_PATH = '/usr/local/lib/python314.zip'; // where the snapshot expects the stdlib; served from memory, read-only
// Node: the worker thread's stack, which bounds wasm's (CPython recurses in C: the parser, repr, nested containers).
// Browsers give a worker their own fixed stack.
export const STACK_MB = 32;
const PAGE = 65536;
const MAX_MODULE = 64 << 20; // python.wasm is 19 MB unpacked

// The few WebAssembly and Worker types used here (the package compiles without the DOM library).
type WasmModule = object; // a compiled WebAssembly.Module: structured-cloneable, so it goes to each worker as it is
interface WasmMemory { readonly buffer: ArrayBufferLike }
const wasmApi = () => (globalThis as unknown as { WebAssembly: { compile(bytes: Uint8Array): Promise<WasmModule> } }).WebAssembly;
interface WebWorker { postMessage(m: unknown): void; terminate(): void; onmessage: ((e: { data: any }) => void) | null; onerror: ((e: { message?: string; preventDefault(): void }) => void) | null }
const WebWorkerClass = () => (globalThis as unknown as { Worker?: new (url: string) => WebWorker }).Worker;

/** The add-on can't run: artifacts missing or altered, no shared memory or workers here. */
export class PythonUnavailable extends Error {}

/** One compiled interpreter (per artifacts folder and memory limit) and the stdlib's bytes. */
export interface Runtime { readonly module: WasmModule; readonly stdlib: Uint8Array }

// Node modules looked up at run time, so bundles for browsers carry no Node import (as web.ts does).
const builtin = (id: string): any => (globalThis as { process?: { getBuiltinModule?: (id: string) => unknown } }).process?.getBuiltinModule?.(id);
const isNode = () => !!(globalThis as { process?: { versions?: { node?: string } } }).process?.versions?.node;

const runtimes = new Map<string, Promise<Runtime>>();

/** The process's compiled interpreter for these artifacts and memory limit: the first call compiles it, later calls reuse
 * it. `folder`: a path or URL holding python.wasm.gz and python314.zip; undefined: the topwater-python add-on's. */
export function runtime(folder: string | URL | undefined, maxMemory: number): Promise<Runtime> {
  const key = `${folder ?? ''}\0${maxMemory}`;
  let rt = runtimes.get(key);
  if (!rt) {
    runtimes.set(key, rt = load(folder, maxMemory));
    rt.catch(() => runtimes.delete(key)); // a failure is not kept: the host may install the add-on and try again
  }
  return rt;
}

async function load(folder: string | URL | undefined, maxMemory: number): Promise<Runtime> {
  if (typeof SharedArrayBuffer === 'undefined' || (globalThis as { crossOriginIsolated?: boolean }).crossOriginIsolated === false)
    throw new PythonUnavailable('this page is not cross-origin isolated, so it has no SharedArrayBuffer: serve it with the headers ' +
      'Cross-Origin-Opener-Policy: same-origin and Cross-Origin-Embedder-Policy: require-corp');
  if (isNode() ? !builtin('node:worker_threads') : !WebWorkerClass())
    throw new PythonUnavailable(isNode() ? 'this Node has no process.getBuiltinModule (Node 22.3 or later)' : 'this runtime has no Web Workers');
  const where = folder ?? await addOn();
  const gz = await readArtifact(where, 'python.wasm.gz');
  const wasm = await gunzip(gz);
  await verify('python.wasm', wasm);
  const stdlib = await readArtifact(where, 'python314.zip');
  await verify('python314.zip', stdlib);
  try {
    return { module: await wasmApi().compile(sharedMemory(wasm, maxMemory)), stdlib };
  } catch (e) {
    throw e instanceof PythonUnavailable ? e : new PythonUnavailable(`python.wasm: ${(e as Error).message}`);
  }
}

/** The module python.wasm.gz holds, read in pieces and refused past MAX_MODULE before its hash is checked: a file that
 * unpacks to gigabytes is not the interpreter. */
async function gunzip(gz: Uint8Array): Promise<Uint8Array> {
  const parts: Uint8Array[] = [];
  let size = 0;
  try {
    const reader = new Blob([gz as Uint8Array<ArrayBuffer>]).stream().pipeThrough(new DecompressionStream('gzip')).getReader();
    for (let r = await reader.read(); !r.done; r = await reader.read()) {
      size += r.value.length;
      if (size > MAX_MODULE) { void reader.cancel(); throw new PythonUnavailable('python.wasm.gz: not the expected file (it unpacks to more than 64 MB)'); }
      parts.push(r.value);
    }
  } catch (e) {
    if (e instanceof PythonUnavailable) throw e;
    throw new PythonUnavailable('python.wasm.gz: not a gzip file');
  }
  return cat(parts);
}

async function addOn(): Promise<string | URL> {
  const name = 'topwater-python'; // a variable, so bundlers don't make the core need the add-on
  try { return (await import(/* @vite-ignore */ name)).artifacts; } catch { throw new PythonUnavailable('install it with npm install topwater-python'); }
}

async function readArtifact(folder: string | URL, name: string): Promise<Uint8Array> {
  const fs = builtin('node:fs/promises');
  const local = fs && (typeof folder === 'string' ? !/^[a-z][a-z0-9+.-]*:\/\//i.test(folder) : folder.protocol === 'file:');
  try {
    if (local) {
      const base = typeof folder === 'string' ? folder.replace(/\/*$/, '/') : new URL(folder.href.replace(/\/*$/, '/'));
      return new Uint8Array(await fs.readFile(typeof base === 'string' ? base + name : new URL(name, base)));
    }
    const base = new URL(String(folder).replace(/\/*$/, '/'), (globalThis as { location?: { href: string } }).location?.href);
    const r = await fetch(new URL(name, base));
    if (!r.ok) throw Object.assign(new Error(`HTTP ${r.status}`), { code: r.status === 404 ? 'ENOENT' : '' });
    return new Uint8Array(await r.arrayBuffer());
  } catch (e) {
    // the file name and the error's kind only: the agent sees this text, and Node's messages hold the host's paths
    const err = e as { code?: string; message?: string };
    throw new PythonUnavailable(`${name}: ${err.code === 'ENOENT' ? ERRNO.ENOENT : err.code || (local ? 'could not be read' : err.message)}`);
  }
}

async function verify(name: string, data: Uint8Array) {
  const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', data as Uint8Array<ArrayBuffer>));
  if ([...digest].map(b => b.toString(16).padStart(2, '0')).join('') !== ARTIFACT_SHA256[name])
    throw new PythonUnavailable(`${name}: not the expected file (its sha256 differs)`);
}

/** The module with its one memory made shared (so this thread can serve the worker's calls) and limited to `maxBytes`: JS
 * has no store limiter, so a guest that grows past it gets a failed memory.grow, which Python reports as MemoryError. */
export function sharedMemory(wasm: Uint8Array, maxBytes: number): Uint8Array {
  let p = 8;
  const leb = () => { let r = 0, s = 0, b: number; do { b = wasm[p++]; r += (b & 0x7f) * 2 ** s; s += 7; } while (b & 0x80); return r; };
  const put = (n: number) => { const out: number[] = []; do { let b = n % 128; n = Math.floor(n / 128); if (n) b |= 0x80; out.push(b); } while (n); return out; };
  while (p < wasm.length) {
    const at = p, id = wasm[p++], size = leb(), start = p;
    if (id === 5) {
      if (leb() !== 1) break;
      const flags = wasm[p++], min = leb();
      if (flags & 1) leb();
      const max = Math.min(65536, Math.floor(maxBytes / PAGE));
      if (max < min) throw new PythonUnavailable(`max memory is below the interpreter's own ${min * PAGE / (1 << 20)} MB`);
      const body = [1, 3, ...put(min), ...put(max)]; // one memory: flags has-max | shared
      const out = new Uint8Array(wasm.length - (start + size - at) + 1 + put(body.length).length + body.length);
      out.set(wasm.subarray(0, at));
      out.set([5, ...put(body.length), ...body], at);
      out.set(wasm.subarray(start + size), at + 1 + put(body.length).length + body.length);
      return out;
    }
    p = start + size;
  }
  throw new PythonUnavailable('python.wasm: no memory section to limit');
}

// ---------- guest state ----------

// WASI preview1 errno values (not Linux's)
export const SUCCESS = 0, E2BIG = 1, EACCES = 2, EBADF = 8, EEXIST = 20, EFAULT = 21, EFBIG = 22, EINVAL = 28, EIO = 29, EISDIR = 31,
  ELOOP = 32, EMFILE = 33, ENAMETOOLONG = 37, ENOENT = 44, ENOSPC = 51, ENOTDIR = 54, ENOTEMPTY = 55, ENOTSUP = 58, EPERM = 63,
  EROFS = 69, ESPIPE = 70;
const BY_TEXT = new Map<string, number>([[ERRNO.ENOENT, ENOENT], [ERRNO.EACCES, EACCES], [ERRNO.EPERM, EPERM], [ERRNO.ENOTDIR, ENOTDIR],
  [ERRNO.EISDIR, EISDIR], [ERRNO.EEXIST, EEXIST], [ERRNO.ENOTEMPTY, ENOTEMPTY], [ERRNO.EINVAL, EINVAL], [ERRNO.ENOSPC, ENOSPC],
  [ERRNO.EBADF, EBADF], [ERRNO.ELOOP, ELOOP]]);
const TEXT = new Map([...BY_TEXT].map(([k, v]) => [v, k]));

const FILETYPE_UNKNOWN = 0, FILETYPE_DIRECTORY = 3, FILETYPE_REGULAR_FILE = 4, FILETYPE_SYMBOLIC_LINK = 7;
const OFLAGS_CREAT = 1, OFLAGS_DIRECTORY = 2, OFLAGS_EXCL = 4, OFLAGS_TRUNC = 8;
const FDFLAGS_APPEND = 1;
const LOOKUP_SYMLINK_FOLLOW = 1;
const RIGHTS_FD_READ = 1 << 1, RIGHTS_FD_WRITE = 1 << 6, RIGHTS_FD_READDIR = 1 << 14, RIGHTS_ALL = 2 ** 30 - 1;
const FSTFLAGS_MTIM = 1 << 2, FSTFLAGS_MTIM_NOW = 1 << 3;
const CLOCK_REALTIME = 0, CLOCK_MONOTONIC = 1;
const EVENTTYPE_CLOCK = 0, EVENTTYPE_FD_READ = 1, EVENTTYPE_FD_WRITE = 2;
const SUBCLOCKFLAGS_ABSTIME = 1;
const ROOT_FD = 3; // the one preopen, "/": the snapshot's wasi-libc cached exactly this table
const MAX_FDS = 1024;
const MAX_PATH = 4096;
const MAX_IOVS = 1024;
const MAX_RANDOM = 1 << 20;
const MAX_SLEEP_SLICE = 250; // ms

class WasiError extends Error {
  constructor(readonly errno: number) { super(String(errno)); }
}

/** proc_exit: the guest ends the call with this status. */
export class ProcExit extends Error {
  constructor(readonly code: number) { super(String(code)); }
}

/** A sleep reached the call's deadline: the guest is stopped as the timer would stop a busy one. */
export class Interrupted extends Error {}

/** stdout plus stderr passed the shell's string limit: the guest is stopped and the call ends as for any command. */
export class OutputFull extends Error {}

/** The WASI errno for a filesystem error, by its text ("path: Permission denied"); EIO for anything else (a mounted file
 * not fetched yet). */
export const errnoOf = (e: ShellError): number => BY_TEXT.get(e.message.slice(e.message.lastIndexOf(': ') + 2)) ?? EIO;

export const errnoText = (errno: number) => TEXT.get(errno) ?? 'Input/output error';

/** A file's contents while the program has it open, shared by every descriptor on it (as one inode's are): saved at close
 * and sync, and before a call names a path (stat, rename, link), so other names see what was written. Once the file was
 * unlinked or replaced (gone), its descriptors keep the contents, which are never saved. */
class FileNode {
  dirty = false;
  handles = 0;
  gone = false;
  constructor(public path: string, public data: Uint8Array, public size: number, public stored: number, public mtime: bigint,
    readonly counted = true) {}
  bytes() { return this.data.subarray(0, this.size); }
  /** Room for `n` bytes, the new ones zero. */
  grow(n: number) {
    if (n > this.data.length) { const d = new Uint8Array(Math.max(n, this.data.length * 2)); d.set(this.bytes()); this.data = d; }
    else if (n > this.size) this.data.fill(0, this.size, n);
    this.size = Math.max(this.size, n);
  }
}

/** A descriptor on a file: its own position and mode over the shared contents. */
class OpenFile {
  pos = 0;
  constructor(readonly node: FileNode, readonly read: boolean, readonly write: boolean, public append: boolean) { node.handles++; }
  get path() { return this.node.path; }
  get data() { return this.node.data; }
  get size() { return this.node.size; } set size(v: number) { this.node.size = v; }
  get dirty() { return this.node.dirty; } set dirty(v: boolean) { this.node.dirty = v; }
  get stored() { return this.node.stored; }
  get mtime() { return this.node.mtime; } set mtime(v: bigint) { this.node.mtime = v; }
  bytes() { return this.node.bytes(); }
  grow(n: number) { this.node.grow(n); }
}

class OpenDir {
  entries: [Uint8Array, number, bigint][] | null = null;
  constructor(readonly path: string) {}
}

const STDIN = 'stdin', STDOUT = 'stdout', STDERR = 'stderr', ROOT = 'root';
type Entry = typeof STDIN | typeof STDOUT | typeof STDERR | typeof ROOT | OpenFile | OpenDir | null;

/** What the HTTP bridge sends with: (method, url, headers, body, timeout ms) to [status code, payload]. */
export type HttpSender = (method: string, url: string, headers: [string, string][], body: Uint8Array, timeoutMs: number) => Promise<[number, Uint8Array]>;

/** Everything one python3 call's instance can reach. `fetch` fetches what mounts were asked for and missed (true when it
 * did); undefined: no mounts. */
export class Guest {
  readonly args: Uint8Array[];
  readonly env: Uint8Array[];
  stdinPos = 0;
  readonly output: [1 | 2, Uint8Array][] = []; // in the order written, so 2>&1 keeps it
  written = 0; // bytes of stdout and stderr
  readonly started = performance.now(); // the monotonic and CPU clocks count from here: nothing about the host's uptime
  readonly fds: Entry[] = [STDIN, STDOUT, STDERR, ROOT];
  buffered = 0; // bytes held in open files' buffers, at most maxMemory in all
  readonly nodes = new Map<string, FileNode>(); // open files by real path
  stopped = false; // the call ended (deadline, exit, output): a host call still waiting on a mount must not touch the files
  pending: Uint8Array | null = null;
  readonly writeBackErrors: [string, number][] = [];
  readonly notes: string[] = [];
  memory: WasmMemory | null = null;

  constructor(readonly fs: VirtualFileSystem, args: string[], env: [string, string][], readonly stdin: Uint8Array,
    readonly outputCap: number, readonly maxMemory: number, readonly stdlib: Uint8Array, readonly deadline: number,
    readonly http: HttpSender, readonly fetch?: () => Promise<boolean>, readonly realtime: () => number = Date.now) {
    this.args = args.map(a => encodeText(a).filter(b => b !== 0));
    this.env = env.filter(([k, v]) => k && !k.includes('=') && !(k + v).includes('\0')).map(([k, v]) => encodeText(`${k}=${v}`));
  }

  // -- descriptors --

  alloc(entry: Entry): number {
    for (let i = ROOT_FD + 1; i < this.fds.length; i++) if (this.fds[i] === null) { this.fds[i] = entry; return i; }
    if (this.fds.length >= MAX_FDS) throw new WasiError(EMFILE);
    this.fds.push(entry);
    return this.fds.length - 1;
  }

  fd(fd: number): Exclude<Entry, null> {
    const e = fd >= 0 && fd < this.fds.length ? this.fds[fd] : null;
    if (e === null) throw new WasiError(EBADF);
    return e;
  }

  /** The absolute path for a guest path relative to a directory descriptor; ".." stops at "/" (the whole sandbox). */
  resolve(dirfd: number, raw: Uint8Array): string {
    if (raw.length === 0) throw new WasiError(ENOENT);
    if (raw.length > MAX_PATH) throw new WasiError(ENAMETOOLONG);
    if (raw.includes(0)) throw new WasiError(EINVAL);
    const d = this.fd(dirfd);
    const base = d === ROOT ? '/' : d instanceof OpenDir ? d.path : null;
    if (base === null) throw new WasiError(ENOTDIR);
    return this.fs.normalizePhysical(base, decodeBytes(raw));
  }

  /** A filesystem call for the guest: its result, or WasiError with the errno. A mounted file or folder the source has
   * not been asked for yet is fetched (this thread awaits the source while the guest waits) and the call tried once more; a
   * mount's failure is kept for the command to print after the program (the agent may run it again). `final`: the exit
   * flush, which runs after the call ended. */
  async call<T>(fn: () => T, final = false): Promise<T> {
    try { return fn(); } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      let error = e;
      if (errnoOf(e) === EIO && this.fetch && await this.fetch()) {
        if (this.stopped && !final) throw new Interrupted(); // the call ended while it waited: nothing more reaches the files
        try { return fn(); } catch (again) {
          if (!(again instanceof ShellError)) throw again;
          error = again;
        }
      }
      if (errnoOf(error) === EIO && this.fetch) this.notes.push(error.message);
      throw new WasiError(errnoOf(error));
    }
  }

  // -- files --

  reserve(extra: number) {
    if (this.buffered + extra > this.maxMemory) throw new WasiError(ENOSPC);
    this.buffered += extra;
  }

  /** How large the file may grow: what the filesystem has free, plus what it holds of this file already. */
  room(f: OpenFile): number { return this.fs.runAs !== null ? this.fs.capacity - this.fs.usedBytes + f.stored : 2 ** 62; }

  /** The key of a path's open file: its real path (shortcut: hard links are separate keys, so two names of one file opened
   * at once are two buffers; upgrade if programs do that). follow false: the last name itself, as lstat. */
  key(path: string, follow = true): string {
    if (follow) return this.fs.realPath(path, false) ?? path;
    const parent = VirtualFileSystem.getParent(path);
    return (this.fs.realPath(parent) ?? parent).replace(/\/$/, '') + '/' + path.slice(path.lastIndexOf('/') + 1);
  }

  opened(key: string, data: Uint8Array, stored: number, mtime: bigint, read: boolean, write: boolean, append: boolean): OpenFile {
    let node = this.nodes.get(key);
    if (!node) {
      this.reserve(data.length);
      this.nodes.set(key, node = new FileNode(key, data, data.length, stored, mtime));
    }
    return new OpenFile(node, read, write, append);
  }

  release(f: OpenFile) {
    const node = f.node;
    if (--node.handles === 0 && node.counted) {
      this.buffered -= node.size;
      if (this.nodes.get(node.path) === node) this.nodes.delete(node.path);
    }
  }

  async save(f: OpenFile | FileNode, final = false) {
    const node = f instanceof OpenFile ? f.node : f;
    if (!node.dirty || node.gone) return;
    await this.call(() => this.fs.writeBytes(node.path, node.bytes()), final);
    node.dirty = false; node.stored = node.size;
  }

  /** Before a call names a path: open files' writes are saved, so its size, a rename or a link sees them. A save that fails
   * is left for close or exit to report. */
  async sync() {
    for (const node of [...this.nodes.values()]) {
      try { await this.save(node); } catch (e) { if (!(e instanceof WasiError)) throw e; }
    }
  }

  /** The file at key was unlinked or replaced: its open descriptors keep their contents, which are never saved. */
  detach(key: string) {
    const node = this.nodes.get(key);
    if (node) { this.nodes.delete(key); node.gone = true; }
  }

  /** A rename: open files at src (or under it) are now at dst; one open at dst was replaced. */
  moved(src: string, dst: string) {
    this.detach(dst);
    const under = src.replace(/\/$/, '') + '/';
    for (const [key, node] of [...this.nodes]) {
      if (key !== src && !key.startsWith(under)) continue;
      this.nodes.delete(key);
      node.path = dst + key.slice(src.length);
      this.nodes.set(node.path, node);
    }
  }

  /** At exit: files the program wrote but never closed are saved, as a real process's are. */
  async flushAll() {
    for (const node of [...this.nodes.values()]) {
      if (!node.dirty) continue;
      try { await this.save(node, true); } catch (err) {
        if (!(err instanceof WasiError)) throw err;
        this.writeBackErrors.push([node.path, err.errno]);
      }
    }
  }

  writeOutput(which: 1 | 2, data: Uint8Array) {
    if (this.written + data.length > this.outputCap) throw new OutputFull();
    this.written += data.length;
    this.output.push([which, data]);
  }
}

// ---------- wasi functions ----------

/** The guest's memory for one call: it is shared and may have grown since the last one. */
class Mem {
  private readonly u8: Uint8Array;
  private readonly dv: DataView;
  constructor(memory: WasmMemory | null) {
    if (!memory) throw new WasiError(EFAULT);
    this.u8 = new Uint8Array(memory.buffer);
    this.dv = new DataView(memory.buffer);
  }
  /** A copy of the guest's bytes (TextDecoder and the VFS can't take a view of shared memory). */
  check(ptr: number, n: number) {
    if ((ptr >>> 0) + (n >>> 0) > this.u8.length) throw new WasiError(EFAULT);
  }
  read(ptr: number, n: number): Uint8Array {
    this.check(ptr, n);
    return this.u8.slice(ptr >>> 0, (ptr >>> 0) + (n >>> 0));
  }
  write(ptr: number, data: Uint8Array) {
    ptr >>>= 0;
    if (ptr + data.length > this.u8.length) throw new WasiError(EFAULT);
    this.u8.set(data, ptr);
  }
  u32(ptr: number, v: number) { this.write(ptr, le(4, BigInt(v >>> 0))); }
  u64(ptr: number, v: bigint) { this.write(ptr, le(8, BigInt.asUintN(64, v))); }
  getU32(at: number) { return this.dv.getUint32(at, true); }
  path(ptr: number, n: number): Uint8Array {
    if (n >>> 0 > MAX_PATH) throw new WasiError(ENAMETOOLONG);
    return this.read(ptr, n);
  }
  iovs(ptr: number, count: number): [number, number][] {
    if (!(count >= 0 && count <= MAX_IOVS)) throw new WasiError(EINVAL);
    const raw = view(this.read(ptr, count * 8));
    return Array.from({ length: count }, (_, i) => [raw.getUint32(i * 8, true), raw.getUint32(i * 8 + 4, true)]);
  }
  stringTable(items: Uint8Array[], ptrs: number, buf: number) {
    const offsets = new Uint8Array(items.length * 4), blob: Uint8Array[] = [];
    let at = buf >>> 0;
    items.forEach((item, i) => { view(offsets).setUint32(i * 4, at, true); blob.push(item, new Uint8Array(1)); at += item.length + 1; });
    this.write(ptrs, offsets);
    this.write(buf, cat(blob));
  }
}

const view = (b: Uint8Array) => new DataView(b.buffer, b.byteOffset, b.byteLength);
const le = (n: number, v: bigint) => { const b = new Uint8Array(n); for (let i = 0; i < n; i++) b[i] = Number((v >> BigInt(8 * i)) & 0xffn); return b; };
const cat = (parts: Uint8Array[]) => { const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0)); let at = 0; for (const p of parts) { out.set(p, at); at += p.length; } return out; };
/** Little-endian fields: [bytes, value] pairs (0 bytes of value pads). */
const pack = (...fields: [number, number | bigint][]) => cat(fields.map(([n, v]) => le(n, BigInt(v))));
const nowNs = () => BigInt(Math.round((performance.timeOrigin + performance.now()) * 1e6));
/** The monotonic and CPU clocks: nanoseconds since the call started. */
const monoNs = (g: Guest) => BigInt(Math.round((performance.now() - g.started) * 1e6));
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));

/** A 64-bit hash of the path (FNV-1a), odd so it is never 0. */
function ino(path: string): bigint {
  let h = 0xcbf29ce484222325n;
  for (const b of encodeText(path)) h = BigInt.asUintN(64, (h ^ BigInt(b)) * 0x100000001b3n);
  return h | 1n;
}

function filestat(filetype: number, size: number, mtime: bigint, inode: bigint, nlink = 1): Uint8Array {
  return pack([8, 1], [8, inode], [1, filetype], [7, 0], [8, nlink], [8, size], [8, mtime], [8, mtime], [8, mtime]);
}

const ns = (ms: number) => BigInt(Math.round(ms)) * 1_000_000n;

async function statPath(g: Guest, path: string, follow: boolean): Promise<Uint8Array> {
  if (path === STDLIB_PATH) return filestat(FILETYPE_REGULAR_FILE, g.stdlib.length, 0n, ino(path));
  await g.sync();
  const st = await g.call(() => g.fs.stat(path, follow));
  return filestat(st.isSymlink ? FILETYPE_SYMBOLIC_LINK : st.isDirectory ? FILETYPE_DIRECTORY : FILETYPE_REGULAR_FILE, st.size, ns(st.modified), ino(path), st.links);
}

type Arg = any; // i32 parameters arrive as numbers, i64 ones as bigints: each function knows which
type Fn = (g: Guest, m: Mem, ...args: Arg[]) => void | Promise<void>;

/** A WASI function: WasiError becomes its errno, success 0. Exits, timeouts and full output pass through. */
const errno = (fn: Fn) => async (g: Guest, ...args: Arg[]): Promise<number> => {
  try { await fn(g, new Mem(g.memory), ...args); return SUCCESS; } catch (e) {
    if (e instanceof WasiError) return e.errno;
    throw e;
  }
};

const isStdio = (e: Exclude<Entry, null>) => e === STDIN || e === STDOUT || e === STDERR;

async function readInto(g: Guest, m: Mem, fd: number, iovsPtr: number, n: number, offset: number | null): Promise<number> {
  const iovs = m.iovs(iovsPtr, n);
  const want = iovs.reduce((s, [, len]) => s + len, 0);
  const e = g.fd(fd);
  let chunk: Uint8Array;
  if (e === STDIN) {
    if (offset !== null) throw new WasiError(ESPIPE);
    chunk = g.stdin.subarray(g.stdinPos, g.stdinPos + want);
    g.stdinPos += chunk.length;
  } else if (e instanceof OpenFile) {
    if (!e.read) throw new WasiError(EBADF);
    const start = offset ?? e.pos;
    chunk = e.bytes().subarray(start, start + want);
    if (offset === null) e.pos = start + chunk.length;
  } else if (e === STDOUT || e === STDERR) throw new WasiError(EBADF);
  else throw new WasiError(EISDIR);
  let done = 0;
  for (const [ptr, len] of iovs) {
    if (done >= chunk.length) break;
    const piece = chunk.subarray(done, done + len);
    m.write(ptr, piece);
    done += piece.length;
  }
  return done;
}

function writeFrom(g: Guest, m: Mem, fd: number, iovsPtr: number, n: number, offset: number | null): number {
  const iovs = m.iovs(iovsPtr, n);
  for (const [ptr, len] of iovs) m.check(ptr, len); // every range checked before anything changes
  const total = iovs.reduce((sum, [, len]) => sum + len, 0);
  // copied only once the destination has room: 1024 vectors over the whole memory would be terabytes
  const data = () => cat(iovs.map(([ptr, len]) => m.read(ptr, len)));
  const e = g.fd(fd);
  if (e === STDOUT || e === STDERR) {
    if (offset !== null) throw new WasiError(ESPIPE);
    if (g.written + total > g.outputCap) throw new OutputFull();
    g.writeOutput(e === STDOUT ? 1 : 2, data());
    return total;
  }
  if (!(e instanceof OpenFile)) throw new WasiError(e === ROOT || e instanceof OpenDir ? EISDIR : EBADF);
  if (!e.write) throw new WasiError(EBADF);
  const start = e.append ? e.size : offset ?? e.pos;
  const end = start + total;
  if (end > e.size) {
    if (end > g.room(e)) throw new WasiError(ENOSPC);
    g.reserve(end - e.size);
  }
  e.grow(end);
  e.data.set(data(), start);
  e.dirty = true; e.mtime = nowNs();
  if (offset === null) e.pos = end;
  return total;
}

async function entries(g: Guest, d: OpenDir | typeof ROOT): Promise<[Uint8Array, number, bigint][]> {
  const path = d === ROOT ? '/' : d.path;
  const out: [Uint8Array, number, bigint][] = [[encodeText('.'), FILETYPE_DIRECTORY, ino(path)], [encodeText('..'), FILETYPE_DIRECTORY, ino(VirtualFileSystem.getParent(path))]];
  let names = await g.call(() => g.fs.list(path));
  const stdlibName = STDLIB_PATH.slice(STDLIB_PATH.lastIndexOf('/') + 1);
  if (path === VirtualFileSystem.getParent(STDLIB_PATH) && !names.includes(stdlibName)) names = [...names, stdlibName].sort((a, b) => a < b ? -1 : a > b ? 1 : 0);
  for (const name of names) {
    const child = path.replace(/\/$/, '') + '/' + name;
    let kind = FILETYPE_REGULAR_FILE;
    if (child !== STDLIB_PATH) {
      try {
        const st = g.fs.stat(child, false);
        kind = st.isSymlink ? FILETYPE_SYMBOLIC_LINK : st.isDirectory ? FILETYPE_DIRECTORY : FILETYPE_REGULAR_FILE;
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        kind = FILETYPE_UNKNOWN;
      }
    }
    out.push([encodeText(name), kind, ino(child)]);
  }
  return out;
}

const unsupported = async () => ENOTSUP;

/** The guest's imports, by "module.name". Each returns the errno (proc_exit never returns). */
export const IMPORTS: Record<string, (g: Guest, ...args: Arg[]) => Promise<number>> = {
  'wasi_snapshot_preview1.args_sizes_get': errno((g, m, argc, size) => {
    m.u32(argc, g.args.length);
    m.u32(size, g.args.reduce((n, a) => n + a.length + 1, 0));
  }),
  'wasi_snapshot_preview1.args_get': errno((g, m, argv, buf) => m.stringTable(g.args, argv, buf)),
  'wasi_snapshot_preview1.environ_sizes_get': errno((g, m, count, size) => {
    m.u32(count, g.env.length);
    m.u32(size, g.env.reduce((n, e) => n + e.length + 1, 0));
  }),
  'wasi_snapshot_preview1.environ_get': errno((g, m, env, buf) => m.stringTable(g.env, env, buf)),
  'wasi_snapshot_preview1.clock_res_get': errno((_g, m, _id, out) => m.u64(out, 1000n)),
  'wasi_snapshot_preview1.clock_time_get': errno((g, m, clock, _precision, out) => {
    // realtime: the shell's clock (virtual when the shell has one); monotonic and CPU clocks: time since this call started
    m.u64(out, clock === CLOCK_REALTIME ? (g.realtime === Date.now ? nowNs() : ns(g.realtime())) : monoNs(g));
  }),
  'wasi_snapshot_preview1.random_get': errno((_g, m, buf, n) => {
    if (!(n >= 0 && n <= MAX_RANDOM)) throw new WasiError(EINVAL);
    const bytes = new Uint8Array(n);
    for (let i = 0; i < n; i += 65536) crypto.getRandomValues(bytes.subarray(i, i + 65536));
    m.write(buf, bytes);
  }),
  'wasi_snapshot_preview1.sched_yield': async () => SUCCESS,
  'wasi_snapshot_preview1.proc_exit': async (_g, code) => { throw new ProcExit(code); },
  'wasi_snapshot_preview1.sock_accept': unsupported,
  'wasi_snapshot_preview1.sock_recv': unsupported,
  'wasi_snapshot_preview1.sock_send': unsupported,
  'wasi_snapshot_preview1.sock_shutdown': unsupported,

  'wasi_snapshot_preview1.fd_prestat_get': errno((g, m, fd, buf) => {
    if (g.fd(fd) !== ROOT) throw new WasiError(EBADF);
    m.write(buf, pack([1, 0], [3, 0], [4, 1]));
  }),
  'wasi_snapshot_preview1.fd_prestat_dir_name': errno((g, m, fd, buf, n) => {
    if (g.fd(fd) !== ROOT) throw new WasiError(EBADF);
    if (n < 1) throw new WasiError(EINVAL);
    m.write(buf, encodeText('/'));
  }),
  'wasi_snapshot_preview1.fd_fdstat_get': errno((g, m, fd, buf) => {
    const e = g.fd(fd);
    // pipes, not terminals: isatty() is false, as in a bash pipeline
    const [kind, flags, rights] = e === STDIN ? [FILETYPE_UNKNOWN, 0, RIGHTS_FD_READ] : e === STDOUT || e === STDERR ? [FILETYPE_UNKNOWN, 0, RIGHTS_FD_WRITE]
      : e instanceof OpenFile ? [FILETYPE_REGULAR_FILE, e.append ? FDFLAGS_APPEND : 0, RIGHTS_ALL] : [FILETYPE_DIRECTORY, 0, RIGHTS_ALL];
    m.write(buf, pack([1, kind], [1, 0], [2, flags], [4, 0], [8, rights], [8, rights]));
  }),
  'wasi_snapshot_preview1.fd_fdstat_set_flags': errno((g, _m, fd, flags) => {
    const e = g.fd(fd);
    if (e instanceof OpenFile) e.append = !!(flags & FDFLAGS_APPEND);
  }),
  'wasi_snapshot_preview1.fd_filestat_get': errno(async (g, m, fd, buf) => {
    const e = g.fd(fd);
    m.write(buf, e instanceof OpenFile ? filestat(FILETYPE_REGULAR_FILE, e.size, e.mtime, ino(e.path))
      : e === ROOT ? filestat(FILETYPE_DIRECTORY, 4096, 0n, ino('/'))
        : e instanceof OpenDir ? await statPath(g, e.path, true) : filestat(FILETYPE_UNKNOWN, 0, 0n, BigInt(fd + 1)));
  }),
  'wasi_snapshot_preview1.fd_filestat_set_size': errno((g, _m, fd, size64) => {
    const f = g.fd(fd);
    if (!(f instanceof OpenFile)) throw new WasiError(EINVAL);
    if (!f.write) throw new WasiError(EBADF);
    const size = Number(size64);
    if (size < 0) throw new WasiError(EINVAL);
    if (size > g.room(f)) throw new WasiError(EFBIG);
    const current = f.size;
    g.reserve(Math.max(0, size - current));
    g.buffered -= Math.max(0, current - size);
    if (size < current) f.size = size; else f.grow(size);
    f.dirty = true;
  }),
  'wasi_snapshot_preview1.fd_filestat_set_times': async () => SUCCESS,
  'wasi_snapshot_preview1.fd_advise': errno((g, _m, fd) => { g.fd(fd); }),
  'wasi_snapshot_preview1.fd_read': errno(async (g, m, fd, iovs, n, nread) => m.u32(nread, await readInto(g, m, fd, iovs, n, null))),
  'wasi_snapshot_preview1.fd_pread': errno(async (g, m, fd, iovs, n, offset, nread) => {
    if (offset < 0n) throw new WasiError(EINVAL);
    m.u32(nread, await readInto(g, m, fd, iovs, n, Number(offset)));
  }),
  'wasi_snapshot_preview1.fd_write': errno((g, m, fd, iovs, n, nwritten) => m.u32(nwritten, writeFrom(g, m, fd, iovs, n, null))),
  'wasi_snapshot_preview1.fd_pwrite': errno((g, m, fd, iovs, n, offset, nwritten) => {
    if (offset < 0n) throw new WasiError(EINVAL);
    m.u32(nwritten, writeFrom(g, m, fd, iovs, n, Number(offset)));
  }),
  'wasi_snapshot_preview1.fd_seek': errno((g, m, fd, offset, whence, out) => {
    const e = g.fd(fd);
    if (isStdio(e)) throw new WasiError(ESPIPE);
    if (!(e instanceof OpenFile)) throw new WasiError(EBADF);
    const base = whence === 0 ? 0 : whence === 1 ? e.pos : whence === 2 ? e.size : null;
    if (base === null || base + Number(offset) < 0) throw new WasiError(EINVAL);
    e.pos = base + Number(offset);
    m.u64(out, BigInt(e.pos));
  }),
  'wasi_snapshot_preview1.fd_tell': errno((g, m, fd, out) => {
    const e = g.fd(fd);
    if (isStdio(e)) throw new WasiError(ESPIPE);
    if (!(e instanceof OpenFile)) throw new WasiError(EBADF);
    m.u64(out, BigInt(e.pos));
  }),
  'wasi_snapshot_preview1.fd_close': errno(async (g, _m, fd) => {
    const e = g.fd(fd);
    g.fds[fd] = null;
    if (e instanceof OpenFile) {
      g.release(e);
      await g.save(e);
    }
  }),
  'wasi_snapshot_preview1.fd_sync': errno(async (g, _m, fd) => { const e = g.fd(fd); if (e instanceof OpenFile) await g.save(e); }),
  'wasi_snapshot_preview1.fd_datasync': errno(async (g, _m, fd) => { const e = g.fd(fd); if (e instanceof OpenFile) await g.save(e); }),
  'wasi_snapshot_preview1.fd_readdir': errno(async (g, m, fd, buf, n, cookie64, used) => {
    const e = g.fd(fd);
    if (!(e === ROOT || e instanceof OpenDir)) throw new WasiError(ENOTDIR);
    const list = e instanceof OpenDir ? (e.entries ??= await entries(g, e)) : await entries(g, e);
    const cookie = Number(cookie64);
    if (cookie < 0 || n < 0) throw new WasiError(EINVAL);
    const out: Uint8Array[] = [];
    let size = 0;
    for (let i = cookie; i < list.length && size < n; i++) {
      const [name, kind, inode] = list[i];
      const dirent = cat([pack([8, i + 1], [8, inode], [4, name.length], [1, kind], [3, 0]), name]);
      out.push(dirent);
      size += dirent.length;
    }
    const bytes = cat(out).subarray(0, n);
    m.write(buf, bytes);
    m.u32(used, bytes.length);
  }),

  'wasi_snapshot_preview1.path_open': errno(async (g, m, dirfd, _dirflags, pathPtr, pathLen, oflags, rights64, _inheriting, fdflags, out) => {
    const path = g.resolve(dirfd, m.path(pathPtr, pathLen));
    const wantDir = oflags & OFLAGS_DIRECTORY, creat = oflags & OFLAGS_CREAT, excl = oflags & OFLAGS_EXCL, trunc = oflags & OFLAGS_TRUNC;
    const rights = Number(BigInt.asUintN(32, rights64));
    const append = !!(fdflags & FDFLAGS_APPEND);
    const write = !!(rights & RIGHTS_FD_WRITE) || !!trunc || append;
    const read = !!(rights & (RIGHTS_FD_READ | RIGHTS_FD_READDIR)) || !write;
    const fs = g.fs;
    if (path === STDLIB_PATH) {
      if (write || creat) throw new WasiError(EROFS);
      if (wantDir) throw new WasiError(ENOTDIR);
      m.u32(out, g.alloc(new OpenFile(new FileNode(path, g.stdlib, g.stdlib.length, g.stdlib.length, 0n, false), true, false, false)));
      return;
    }
    let st: ReturnType<VirtualFileSystem['stat']> | null = null, why = 0;
    try { st = await g.call(() => fs.stat(path)); } catch (e) {
      if (!(e instanceof WasiError)) throw e;
      why = e.errno;
    }
    if (st?.isDirectory) {
      if (creat && excl) throw new WasiError(EEXIST);
      if (write) throw new WasiError(EISDIR);
      m.u32(out, g.alloc(new OpenDir(path)));
      return;
    }
    if (st) {
      if (wantDir) throw new WasiError(ENOTDIR);
      if (creat && excl) throw new WasiError(EEXIST);
      if (write && !fs.canWrite(path) || read && !fs.canRead(path)) throw new WasiError(EACCES);
      const key = g.key(path), node = g.nodes.get(key);
      let data: Uint8Array = new Uint8Array(0), stored = 0;
      if (trunc) {
        if (st.size || node?.stored) await g.call(() => fs.writeBytes(path, new Uint8Array(0))); // O_TRUNC empties the file now, as open(2) does
        if (node) { g.buffered -= node.size; node.size = 0; node.stored = 0; node.dirty = false; } // and for the descriptors already open on it
      } else if (!node) {
        data = await g.call(() => fs.readBytes(path)); stored = data.length;
      }
      m.u32(out, g.alloc(g.opened(key, data, stored, ns(st.modified), read, write, append)));
      return;
    }
    if (why !== ENOENT || !creat || wantDir) throw new WasiError(why);
    await g.call(() => fs.writeBytes(path, new Uint8Array(0))); // the parent must exist and be writable: the filesystem checks it as for any command
    m.u32(out, g.alloc(g.opened(g.key(path), new Uint8Array(0), 0, nowNs(), read, write, append)));
  }),
  'wasi_snapshot_preview1.path_filestat_get': errno(async (g, m, dirfd, flags, pathPtr, pathLen, buf) => {
    const path = g.resolve(dirfd, m.path(pathPtr, pathLen));
    m.write(buf, await statPath(g, path, !!(flags & LOOKUP_SYMLINK_FOLLOW)));
  }),
  'wasi_snapshot_preview1.path_filestat_set_times': errno(async (g, m, dirfd, flags, pathPtr, pathLen, _atim, mtim, fst) => {
    const path = g.resolve(dirfd, m.path(pathPtr, pathLen));
    if (path === STDLIB_PATH) throw new WasiError(EROFS);
    const when = fst & FSTFLAGS_MTIM_NOW ? g.realtime() : fst & FSTFLAGS_MTIM ? Number(mtim / 1_000_000n) : null;
    await g.sync();
    await g.call(() => g.fs.stat(path));
    if (when !== null) await g.call(() => g.fs.setModifiedTime(path, when, !!(flags & LOOKUP_SYMLINK_FOLLOW)));
  }),
  'wasi_snapshot_preview1.path_create_directory': errno(async (g, m, dirfd, pathPtr, pathLen) => {
    const path = g.resolve(dirfd, m.path(pathPtr, pathLen));
    if (path === STDLIB_PATH || g.fs.exists(path, false)) throw new WasiError(EEXIST);
    await g.call(() => g.fs.mkdir(path));
  }),
  'wasi_snapshot_preview1.path_remove_directory': errno(async (g, m, dirfd, pathPtr, pathLen) => {
    const path = g.resolve(dirfd, m.path(pathPtr, pathLen));
    if (path === '/') throw new WasiError(EACCES);
    if (!(await g.call(() => g.fs.stat(path, false))).isDirectory) throw new WasiError(ENOTDIR);
    await g.call(() => g.fs.delete(path)); // refuses a directory with entries (ENOTEMPTY)
  }),
  'wasi_snapshot_preview1.path_unlink_file': errno(async (g, m, dirfd, pathPtr, pathLen) => {
    const path = g.resolve(dirfd, m.path(pathPtr, pathLen));
    if (path === STDLIB_PATH) throw new WasiError(EROFS);
    if ((await g.call(() => g.fs.stat(path, false))).isDirectory) throw new WasiError(EISDIR);
    const key = g.key(path, false);
    await g.call(() => g.fs.delete(path));
    g.detach(key);
  }),
  'wasi_snapshot_preview1.path_rename': errno(async (g, m, fd1, oldPtr, oldLen, fd2, newPtr, newLen) => {
    const src = g.resolve(fd1, m.path(oldPtr, oldLen));
    const dst = g.resolve(fd2, m.path(newPtr, newLen));
    if (src === STDLIB_PATH || dst === STDLIB_PATH) throw new WasiError(EROFS);
    const fs = g.fs;
    await g.sync();
    const s = await g.call(() => fs.stat(src, false));
    const d = fs.exists(dst, false) ? await g.call(() => fs.stat(dst, false)) : null;
    const srcKey = g.key(src, false), dstKey = g.key(dst, false);
    if (srcKey === dstKey || d && fs.sameFile(src, dst, false)) return; // rename(2): one file under both names stays as it is
    if (s.isDirectory && dstKey.startsWith(srcKey.replace(/\/$/, '') + '/')) throw new WasiError(EINVAL); // into itself
    if (d?.isDirectory) { // a directory replaces an empty one; anything else can't
      if (!s.isDirectory) throw new WasiError(EISDIR);
      await g.call(() => fs.delete(dst));
    } else if (d && s.isDirectory) throw new WasiError(ENOTDIR);
    await g.call(() => fs.move(src, dst));
    g.moved(srcKey, dstKey);
  }),
  'wasi_snapshot_preview1.path_symlink': errno(async (g, m, oldPtr, oldLen, dirfd, newPtr, newLen) => {
    const raw = m.path(oldPtr, oldLen);
    if (raw.includes(0)) throw new WasiError(EINVAL);
    const target = decodeBytes(raw);
    const link = g.resolve(dirfd, m.path(newPtr, newLen));
    if (link === STDLIB_PATH) throw new WasiError(EEXIST);
    await g.call(() => g.fs.createSymlink(link, target));
  }),
  'wasi_snapshot_preview1.path_readlink': errno(async (g, m, dirfd, pathPtr, pathLen, buf, n, used) => {
    const path = g.resolve(dirfd, m.path(pathPtr, pathLen));
    const target = encodeText(await g.call(() => g.fs.readLink(path))).subarray(0, Math.max(0, n));
    m.write(buf, target);
    m.u32(used, target.length);
  }),
  'wasi_snapshot_preview1.path_link': errno(async (g, m, fd1, flags, oldPtr, oldLen, fd2, newPtr, newLen) => {
    const src = g.resolve(fd1, m.path(oldPtr, oldLen));
    const dst = g.resolve(fd2, m.path(newPtr, newLen));
    if (src === STDLIB_PATH || dst === STDLIB_PATH) throw new WasiError(EROFS);
    await g.sync();
    if ((await g.call(() => g.fs.stat(src, !!(flags & LOOKUP_SYMLINK_FOLLOW)))).isDirectory) throw new WasiError(EPERM); // link(2): no hard links to directories
    await g.call(() => g.fs.createHardLink(src, dst, !!(flags & LOOKUP_SYMLINK_FOLLOW)));
  }),
  'wasi_snapshot_preview1.poll_oneoff': errno(async (g, m, subs, events, n, nevents) => {
    if (!(n > 0 && n <= 64)) throw new WasiError(EINVAL);
    const raw = view(m.read(subs, n * 48));
    const ready: [bigint, number, number, number][] = [];
    let wait: [bigint, bigint] | null = null;
    for (let i = 0; i < n; i++) {
      const userdata = raw.getBigUint64(i * 48, true), kind = raw.getUint8(i * 48 + 8);
      if (kind === EVENTTYPE_CLOCK) {
        const clock = raw.getUint32(i * 48 + 16, true), flags = raw.getUint16(i * 48 + 40, true);
        let timeout = raw.getBigUint64(i * 48 + 24, true);
        if (flags & SUBCLOCKFLAGS_ABSTIME) {
          const now = clock === CLOCK_REALTIME ? nowNs() : monoNs(g);
          timeout = timeout > now ? timeout - now : 0n;
        }
        if (wait === null || timeout < wait[0]) wait = [timeout, userdata];
      } else if (kind === EVENTTYPE_FD_READ || kind === EVENTTYPE_FD_WRITE) {
        const fd = raw.getUint32(i * 48 + 16, true);
        try {
          const e = g.fd(fd);
          ready.push([userdata, SUCCESS, kind, e === STDIN ? g.stdin.length - g.stdinPos : e instanceof OpenFile ? Math.max(0, e.size - e.pos) : 0]);
        } catch (err) {
          if (!(err instanceof WasiError)) throw err;
          ready.push([userdata, err.errno, kind, 0]);
        }
      } else throw new WasiError(EINVAL);
    }
    if (!ready.length && wait !== null) {
      // a real sleep on the monotonic clock, rounded up so it never ends early, in slices, never past the call's
      // deadline (where the guest is stopped)
      const until = performance.now() + Number((wait[0] + 999_999n) / 1_000_000n);
      for (let now = performance.now(); now < until; now = performance.now()) {
        const left = g.deadline - Date.now();
        if (left <= 0) throw new Interrupted();
        await sleep(Math.min(until - now, left, MAX_SLEEP_SLICE));
      }
      ready.push([wait[1], SUCCESS, EVENTTYPE_CLOCK, 0]);
    }
    m.write(events, cat(ready.map(([u, err, k, nb]) => pack([8, u], [2, err], [1, k], [5, 0], [8, nb], [2, 0], [6, 0]))));
    m.u32(nevents, ready.length);
  }),

  'bashkit.http_request': (g, ptr, n, timeoutMs, outLen) => httpRequest(g, ptr, n, timeoutMs, outLen),
  'bashkit.http_take': async (g, buf, n) => httpTake(g, buf, n),
};

// ---------- http bridge ----------

// bashkit.http_request(req_ptr, req_len, timeout_ms, out_len_ptr) -> status; bashkit.http_take(buf, len) -> status.
// The request is length-prefixed little-endian: method, url, u32 header count, (name, value)..., body (each a u32
// length then the bytes). A success payload is u32 status, u32 header count, (name, value)..., body; a failure's is the
// error text. http_request stores the payload and writes its size; http_take copies it into a buffer of exactly that size.
export const HTTP_OK = 0, HTTP_NETWORK = 1, HTTP_TIMEOUT = 2, HTTP_INVALID = 3, HTTP_FAULT = 4;
const HTTP_METHODS = ['GET', 'POST', 'PUT', 'DELETE', 'HEAD', 'PATCH'];
const MAX_URL = 8192;
const MAX_HEADERS = 128;
const MAX_HEADER_BYTES = 64 * 1024;
const MAX_REQUEST_BODY = 16 << 20;
const MAX_ERROR_TEXT = 512;
const utf8 = new TextDecoder('utf-8', { fatal: true });

export interface HttpRequest { method: string; url: string; headers: [string, string][]; body: Uint8Array }

/** The request in a guest record. Throws an Error with the reason. */
export function decodeRequest(buf: Uint8Array): HttpRequest {
  let at = 0;
  const dv = view(buf);
  const u32 = () => {
    if (at + 4 > buf.length) throw new Error('truncated request');
    at += 4;
    return dv.getUint32(at - 4, true);
  };
  const take = (limit: number, what: string) => {
    const n = u32();
    if (n > limit) throw new Error(`${what} too long`);
    if (at + n > buf.length) throw new Error('truncated request');
    at += n;
    return buf.subarray(at - n, at);
  };
  const text = (limit: number, what: string) => {
    try { return utf8.decode(take(limit, what)); } catch (e) {
      if (e instanceof TypeError) throw new Error(`${what} is not UTF-8`);
      throw e;
    }
  };
  const method = text(16, 'method').toUpperCase();
  if (!HTTP_METHODS.includes(method)) throw new Error(`unsupported HTTP method: ${method}`);
  const url = text(MAX_URL, 'URL');
  if (/[\x00-\x20\x7f]/.test(url)) throw new Error('URL contains control characters or spaces');
  const count = u32();
  if (count > MAX_HEADERS) throw new Error('too many headers');
  const headers: [string, string][] = [];
  let size = 0;
  for (let i = 0; i < count; i++) {
    const name = text(MAX_HEADER_BYTES, 'header name'), value = text(MAX_HEADER_BYTES, 'header value');
    size += name.length + value.length;
    if (size > MAX_HEADER_BYTES) throw new Error('headers too large');
    if (!/^[A-Za-z0-9!#$%&'*+\-.^_`|~]+$/.test(name)) throw new Error('invalid header name');
    if (/[\r\n\0]/.test(value)) throw new Error('invalid header value');
    headers.push([name, value.trim()]);
  }
  const body = take(MAX_REQUEST_BODY, 'request body');
  if (at !== buf.length) throw new Error('trailing bytes in request');
  return { method, url, headers, body: body.slice() };
}

export function encodeResponse(status: number, headers: [string, string][], body: Uint8Array): Uint8Array {
  const out: Uint8Array[] = [pack([4, status], [4, headers.length])];
  for (const [k, v] of headers) for (const s of [encodeText(k), encodeText(v)]) out.push(pack([4, s.length]), s);
  out.push(pack([4, body.length]), body);
  return cat(out);
}

export const errorPayload = (text: string) => new TextEncoder().encode(text).subarray(0, MAX_ERROR_TEXT);

async function httpRequest(g: Guest, ptr: number, n: number, timeoutMs: bigint, outLen: number): Promise<number> {
  let raw: Uint8Array;
  try { raw = new Mem(g.memory).read(ptr, n); } catch (e) {
    if (e instanceof WasiError) return HTTP_FAULT;
    throw e;
  }
  g.pending = null;
  let code: number, payload: Uint8Array;
  let req: HttpRequest | null = null;
  try { req = decodeRequest(raw); } catch (e) { code = HTTP_INVALID; payload = errorPayload((e as Error).message); }
  if (req) {
    const left = Math.max(0, g.deadline - Date.now());
    [code, payload] = await g.http(req.method, req.url, req.headers, req.body, timeoutMs < 0n ? left : Math.min(left, Number(timeoutMs))); // the web policy counts and limits requests
    if (code !== HTTP_OK) payload = errorPayload(decodeBytes(payload));
  }
  try { new Mem(g.memory).u32(outLen, payload!.length); } catch (e) {
    if (e instanceof WasiError) return HTTP_FAULT;
    throw e;
  }
  g.pending = payload!;
  return code!;
}

function httpTake(g: Guest, buf: number, n: number): number {
  const payload = g.pending;
  g.pending = null;
  if (payload === null || n !== payload.length) return HTTP_FAULT;
  try { new Mem(g.memory).write(buf, payload); } catch (e) {
    if (e instanceof WasiError) return HTTP_FAULT;
    throw e;
  }
  return HTTP_OK;
}

// ---------- running a call ----------

/** How a call ended: code (the exit status) unless timedOut, stackOverflow, aborted (abort()), or trap (another fatal
 * error's text). */
export class Outcome {
  constructor(readonly code = 1, readonly timedOut = false, readonly stackOverflow = false, readonly trap: string | null = null,
    readonly aborted = false) {}
}

// The worker: plain JavaScript run from a string (no file to locate, in Node or behind a bundler). Each import posts
// its arguments to the shell's thread and waits on the flag: 1 when the answer (in slot 1) is there, 2 when the host
// ended the call. The guest's memory goes to the shell's thread once, before anything runs.
const WORKER_SOURCE = `
const node = typeof self === 'undefined';
const port = node ? require('node:worker_threads').parentPort : self;
const STOP = {};
const start = ({ module, control }) => {
  const flag = new Int32Array(control);
  const imports = {};
  WebAssembly.Module.imports(module).forEach(({ module: mod, name }) => {
    const call = mod + '.' + name;
    (imports[mod] ??= {})[name] = (...args) => {
      Atomics.store(flag, 0, 0);
      port.postMessage({ call, args });
      Atomics.wait(flag, 0, 0);
      if (Atomics.load(flag, 0) === 2) throw STOP;
      return flag[1];
    };
  });
  try {
    const exports = new WebAssembly.Instance(module, imports).exports;
    port.postMessage({ memory: exports.memory });
    if (exports._initialize) exports._initialize();
    port.postMessage({ done: exports.bashkit_run() });
  } catch (e) {
    // abort(): wasm's unreachable, the C library's last resort (SIGABRT for a real process)
    if (e !== STOP) port.postMessage({ error: String(e && e.message || e).split('\\n')[0].slice(0, 200), stack: e instanceof RangeError,
      abort: e instanceof WebAssembly.RuntimeError && /unreachable/i.test(e.message) });
  }
};
if (node) port.once('message', start); else self.onmessage = e => start(e.data);
`;

interface GuestWorker { post(m: unknown): void; on(f: (m: any) => void, failed: (why: string) => void): void; terminate(): void }

function spawn(): GuestWorker {
  const wt = builtin('node:worker_threads');
  if (wt) {
    const w = new wt.Worker(WORKER_SOURCE, { eval: true, resourceLimits: { stackSizeMb: STACK_MB } });
    w.unref(); // a call that is still being stopped never keeps the host's process alive
    return {
      post: m => w.postMessage(m),
      on: (f, failed) => { w.on('message', f); w.on('error', (e: Error) => failed(e.message)); w.on('exit', () => failed('the interpreter stopped')); },
      terminate: () => void w.terminate(),
    };
  }
  const url = URL.createObjectURL(new Blob([WORKER_SOURCE], { type: 'text/javascript' }));
  const w = new (WebWorkerClass()!)(url);
  URL.revokeObjectURL(url);
  return {
    post: m => w.postMessage(m),
    on: (f, failed) => { w.onmessage = e => f(e.data); w.onerror = e => { e.preventDefault(); failed(e.message || 'the interpreter stopped'); }; },
    terminate: () => w.terminate(),
  };
}

/** Runs bashkit_run on a fresh instance in a new worker, serving its calls here until it ends, the deadline passes or
 * its output fills up (rejects with OutputFull); then saves the files the program left open. */
export function run(rt: Runtime, guest: Guest): Promise<Outcome> {
  const control = new SharedArrayBuffer(8);
  const flag = new Int32Array(control);
  const worker = spawn();
  let finished = false, timer: ReturnType<typeof setTimeout> | undefined;
  return new Promise<Outcome>((resolve, reject) => {
    const finish = (outcome: Outcome | Error) => {
      if (finished) return;
      finished = true;
      clearTimeout(timer);
      guest.stopped = true; // a call still awaiting a mount stops there
      Atomics.store(flag, 0, 2); // a worker waiting on a call unwinds; one running is stopped
      Atomics.notify(flag, 0);
      worker.terminate();
      guest.memory = null;
      guest.flushAll().then(() => outcome instanceof Error ? reject(outcome) : resolve(outcome), reject);
    };
    const left = guest.deadline - Date.now();
    if (left <= 0) return finish(new Outcome(1, true));
    if (Number.isFinite(left)) timer = setTimeout(() => finish(new Outcome(1, true)), Math.min(left + 2, 2 ** 31 - 1));
    worker.on(async m => {
      if (finished) return;
      if (m.memory) guest.memory = m.memory;
      else if (m.call !== undefined) {
        const fn = IMPORTS[m.call];
        let result: number;
        try { result = fn ? await fn(guest, ...m.args) : ENOTSUP; } catch (e) {
          return finish(e instanceof ProcExit ? new Outcome(e.code) : e instanceof Interrupted ? new Outcome(1, true)
            : e instanceof OutputFull ? e : new Outcome(1, false, false, `host: ${(e as Error)?.message ?? e}`.slice(0, 200)));
        }
        if (finished) return;
        flag[1] = result;
        Atomics.store(flag, 0, 1);
        Atomics.notify(flag, 0);
      } else if (m.done !== undefined) finish(new Outcome(m.done));
      else if (m.error !== undefined) finish(Date.now() >= guest.deadline ? new Outcome(1, true) : m.abort ? new Outcome(1, false, false, null, true)
        : new Outcome(1, false, m.stack, m.stack ? null : m.error));
    }, why => finish(Date.now() >= guest.deadline ? new Outcome(1, true) : new Outcome(1, false, false, why)));
    worker.post({ module: rt.module, control });
  });
}
