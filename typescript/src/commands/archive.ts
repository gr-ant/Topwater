// tar, gzip/gunzip/zcat and zip/unzip over the virtual filesystem. Archive bytes cross pipes as pipe text (bytes.ts).
// Archives are untrusted: extraction never leaves the target directory (`..` is refused, absolute names are made
// relative, nothing is written under or hard-linked through a symbolic link the archive made, hard links only reach entries
// already there), and one extraction or decompression stops past ctx.shell.options.maxExtractBytes / maxExtractEntries.

import { Args, ShellError, reason, type CommandContext } from '../command.js';
import { concatBytes, decodeBytes, encodeText } from '../bytes.js';
import { InflateError, OutputLimitError, gunzip, inflateRaw } from '../inflate.js';
import { deflate } from '../compress.js';
import { VirtualFileSystem } from '../vfs.js';
import { globRegex } from '../glob.js';
import type { Shell } from '../shell.js';
import { ZipFormatError, crc32, dosTime, fromDos, readZip, writeZip, type ZipEntry } from './zipfile.js';

// ---------- shared pieces ----------

/** An extraction limit was hit: unlike other errors while extracting, it ends the command. */
class ExtractLimit extends ShellError {}

/** What one extraction or decompression may still produce. */
class Budget {
  bytes: number;
  entries: number;
  constructor(private readonly max: { maxExtractBytes: number; maxExtractEntries: number }, private readonly code: number) {
    this.bytes = max.maxExtractBytes;
    this.entries = max.maxExtractEntries;
  }
  entry() { if (--this.entries < 0) throw new ExtractLimit(`archive exceeds the ${this.max.maxExtractEntries}-entry extraction limit; stopping`, this.code); }
  charge(n: number) { if ((this.bytes -= n) < 0) throw this.limit(); }
  limit() { return new ExtractLimit(`output exceeds the ${this.max.maxExtractBytes}-byte extraction limit; stopping`, this.code); }
}

const isGzip = (d: Uint8Array) => d.length >= 2 && d[0] === 0x1f && d[1] === 0x8b;
const bytesOf = (text: string) => encodeText(text);
const writeBinary = (ctx: CommandContext, data: Uint8Array) => ctx.stdout.write(decodeBytes(data));
const concat = concatBytes;

/** Inflates with a cap on the output, so a bomb never allocates more than the budget. */
function inflate(kind: 'gunzip' | 'raw', data: Uint8Array, budget: Budget): Uint8Array | { error: string } {
  try {
    const out = (kind === 'gunzip' ? gunzip : inflateRaw)(data, Math.max(1, budget.bytes));
    budget.charge(out.length);
    return out;
  } catch (e) {
    if (e instanceof OutputLimitError) throw budget.limit();
    if (!(e instanceof InflateError)) throw e;
    if (e.kind === 'eof') return { error: 'unexpected end of file' };
    if (e.kind === 'crc') return { error: 'invalid compressed data--crc error' };
    return { error: 'invalid compressed data--format violated' };
  }
}
const failed = (r: Uint8Array | { error: string }): r is { error: string } => 'error' in r;

const pad = (n: number | string, width: number) => String(n).padStart(width);
const two = (n: number) => String(n).padStart(2, '0');
const stamp = (ms: number) => { const d = new Date(ms); return `${d.getUTCFullYear()}-${two(d.getUTCMonth() + 1)}-${two(d.getUTCDate())} ${two(d.getUTCHours())}:${two(d.getUTCMinutes())}`; };

/** The leading part GNU tar strips from a member name: leading slashes, or everything through the last `..`. */
function tarPrefix(name: string): string {
  let cut = 0;
  while (cut < name.length && name[cut] === '/') cut++;
  for (let i = 0; i < name.length;) {
    let j = name.indexOf('/', i);
    if (j < 0) j = name.length;
    if (name.slice(i, j) === '..') { cut = j; while (cut < name.length && name[cut] === '/') cut++; }
    i = j + 1;
  }
  return name.slice(0, cut);
}

/** Directory path joined under `dir`, without escaping it (callers have removed `..`). */
const under = (dir: string, parts: string[]) => VirtualFileSystem.normalize(dir, parts.join('/'));
const nonEmpty = (parts: string[]) => parts.filter(p => p !== '');

// ---------- tar: writing ----------

const BLOCK = 512;
const enc = new TextEncoder();
const dec = new TextDecoder();

function setString(block: Uint8Array, off: number, len: number, s: string) { block.set(enc.encode(s).subarray(0, len), off); }
function setOctal(block: Uint8Array, off: number, len: number, n: number) { setString(block, off, len, Math.max(0, Math.floor(n)).toString(8).padStart(len - 1, '0')); }

/** type: file, hard link (`link` is the member it names), symbolic link (`link` is its target) or directory */
interface TarMember { name: string; type: '0' | '1' | '2' | '5'; mode: number; mtime: number; owner: string; data: Uint8Array; link?: string }

function paxRecord(key: string, value: string): Uint8Array {
  const body = ` ${key}=${value}\n`;
  let len = enc.encode(body).length + 1;
  while (String(len).length + enc.encode(body).length !== len) len = String(len).length + enc.encode(body).length;
  return enc.encode(`${len}${body}`);
}

function tarHeader(name: string, type: string, size: number, m: TarMember): Uint8Array {
  const b = new Uint8Array(BLOCK);
  let nameBytes = enc.encode(name), prefix = '';
  if (nameBytes.length > 100) {
    // ustar splits a long name at a slash into prefix and name
    for (let i = name.indexOf('/'); i >= 0; i = name.indexOf('/', i + 1)) {
      const p = enc.encode(name.slice(0, i)), rest = enc.encode(name.slice(i + 1));
      if (p.length <= 155 && rest.length <= 100 && rest.length > 0) { prefix = name.slice(0, i); nameBytes = rest; }
    }
  }
  b.set(nameBytes.subarray(0, 100), 0);
  setOctal(b, 100, 8, m.mode & 0o7777);
  const id = m.owner === 'root' ? 0 : 1000;
  setOctal(b, 108, 8, id);
  setOctal(b, 116, 8, id);
  setOctal(b, 124, 12, size);
  setOctal(b, 136, 12, m.mtime);
  b[156] = type.charCodeAt(0);
  setString(b, 257, 6, 'ustar\0');
  setString(b, 263, 2, '00');
  setString(b, 265, 32, m.owner);
  setString(b, 297, 32, m.owner);
  if (type !== 'x' && m.link !== undefined) setString(b, 157, 100, m.link);
  setString(b, 345, 155, prefix);
  b.fill(0x20, 148, 156);
  let sum = 0;
  for (const x of b) sum += x;
  setString(b, 148, 7, sum.toString(8).padStart(6, '0') + '\0');
  b[155] = 0x20;
  return b;
}

function tarBlocks(m: TarMember): Uint8Array[] {
  const parts: Uint8Array[] = [];
  const nameBytes = enc.encode(m.name);
  const fits = nameBytes.length <= 100 || (() => {
    for (let i = m.name.indexOf('/'); i >= 0; i = m.name.indexOf('/', i + 1))
      if (enc.encode(m.name.slice(0, i)).length <= 155 && enc.encode(m.name.slice(i + 1)).length <= 100 && i + 1 < m.name.length) return true;
    return false;
  })();
  const longLink = m.link !== undefined && enc.encode(m.link).length > 100;
  if (!fits || longLink) {
    const rec = concat([...(fits ? [] : [paxRecord('path', m.name)]), ...(longLink ? [paxRecord('linkpath', m.link!)] : [])]);
    parts.push(tarHeader('PaxHeader', 'x', rec.length, m), rec, new Uint8Array((BLOCK - rec.length % BLOCK) % BLOCK));
  }
  parts.push(tarHeader(fits ? m.name : m.name.slice(0, 99), m.type, m.data.length, m), m.data, new Uint8Array((BLOCK - m.data.length % BLOCK) % BLOCK));
  return parts;
}

// ---------- tar: reading ----------

interface TarEntry { name: string; type: string; mode: number; mtime: number; size: number; link: string; uname: string; gname: string; uid: number; gid: number; data: Uint8Array }
class TarFormatError extends Error {}

const cstr = (b: Uint8Array) => { const z = b.indexOf(0); return dec.decode(z < 0 ? b : b.subarray(0, z)); };
function parseNumber(b: Uint8Array): number {
  if (b[0] & 0x80) { let n = b[0] & 0x7f; for (let i = 1; i < b.length; i++) n = n * 256 + b[i]; return n; }
  const s = cstr(b).trim();
  if (s === '') return 0;
  if (!/^[0-7]+$/.test(s)) throw new TarFormatError('bad number');
  return parseInt(s, 8);
}
function parsePax(data: Uint8Array): Map<string, string> {
  const r = new Map<string, string>();
  let pos = 0;
  while (pos < data.length) {
    const sp = data.indexOf(0x20, pos);
    if (sp < 0) break;
    const len = Number(dec.decode(data.subarray(pos, sp)));
    if (!(len > sp - pos + 1) || pos + len > data.length) break;
    const rec = dec.decode(data.subarray(sp + 1, pos + len - 1));
    const eq = rec.indexOf('=');
    if (eq > 0) r.set(rec.slice(0, eq), rec.slice(eq + 1));
    pos += len;
  }
  return r;
}

/** Reads the members of a tar archive; throws TarFormatError (with `first` set for the first header) on a damaged one. */
function* readTar(data: Uint8Array): Generator<TarEntry> {
  let pos = 0, first = true;
  let pax: Map<string, string> | null = null, longName: string | null = null, longLink: string | null = null;
  while (pos < data.length) {
    if (pos + BLOCK > data.length) throw new TarFormatError(first ? 'not a tar archive' : 'eof');
    const h = data.subarray(pos, pos + BLOCK);
    if (h.every(x => x === 0)) return;
    let stored: number;
    try { stored = parseNumber(h.subarray(148, 156)); } catch { throw new TarFormatError(first ? 'not a tar archive' : 'eof'); }
    let sum = 0;
    for (let i = 0; i < BLOCK; i++) sum += i >= 148 && i < 156 ? 0x20 : h[i];
    if (sum !== stored) throw new TarFormatError(first ? 'not a tar archive' : 'eof');
    first = false;
    let size: number;
    try { size = parseNumber(h.subarray(124, 136)); } catch { throw new TarFormatError('eof'); }
    const type = h[156] === 0 ? '0' : String.fromCharCode(h[156]);
    const body = data.subarray(pos + BLOCK, pos + BLOCK + size);
    if (body.length < size) throw new TarFormatError('eof');
    pos += BLOCK + Math.ceil(size / BLOCK) * BLOCK;
    if (type === 'g') continue;
    if (type === 'x') { pax = parsePax(body); continue; }
    if (type === 'L') { longName = cstr(body); continue; }
    if (type === 'K') { longLink = cstr(body); continue; }
    const ustar = cstr(h.subarray(257, 262)) === 'ustar';
    let name = cstr(h.subarray(0, 100));
    if (ustar && h[345] !== 0) name = cstr(h.subarray(345, 500)) + '/' + name;
    name = pax?.get('path') ?? longName ?? name;
    const link = pax?.get('linkpath') ?? longLink ?? cstr(h.subarray(157, 257));
    const mtime = pax?.has('mtime') ? Math.floor(Number(pax.get('mtime'))) : parseNumber(h.subarray(136, 148));
    const realSize = pax?.has('size') ? Number(pax.get('size')) : size;
    const entry: TarEntry = {
      name, type, link, mtime, size: realSize, data: body, mode: parseNumber(h.subarray(100, 108)),
      uid: parseNumber(h.subarray(108, 116)), gid: parseNumber(h.subarray(116, 124)),
      uname: ustar ? pax?.get('uname') ?? cstr(h.subarray(265, 297)) : '', gname: ustar ? pax?.get('gname') ?? cstr(h.subarray(297, 329)) : '',
    };
    pax = null; longName = null; longLink = null;
    yield entry;
  }
}

// ---------- tar ----------

const TAR_USAGE = "\nTry 'tar --help' or 'tar --usage' for more information.";
const isFileType = (t: string) => t === '0' || t === '7';

async function tar(ctx: CommandContext): Promise<number> {
  let args = [...ctx.args];
  if (args.length > 0 && args[0].length > 0 && args[0][0] !== '-') {
    // old style: `tar czf out.tgz dir`, where f and C take the words after the bundle, in order
    const rest = args.slice(1);
    args = [];
    for (const c of ctx.args[0]) {
      args.push('-' + c);
      if (c !== 'f' && c !== 'C') continue;
      if (rest.length === 0) throw new ShellError(`Old option '${c}' requires an argument.${TAR_USAGE}`, 2);
      args.push(rest.shift()!);
    }
    args.push(...rest);
  }
  const a = Args.parse(args, 'cxtzvOpo', 'fC\u0001',
    'create=c,extract=x,get=x,list=t,gzip=z,gunzip=z,ungzip=z,verbose=v,to-stdout=O,file=f,directory=C,strip-components=\u0001,preserve-permissions=p,no-same-owner=o');
  const modes = [...'cxt'].filter(c => a.has(c)).length;
  if (modes === 0) throw new ShellError("You must specify one of the '-Acdtrux', '--delete' or '--test-label' options" + TAR_USAGE, 2);
  if (modes > 1) throw new ShellError("You may not specify more than one '-Acdtrux', '--delete' or  '--test-label' option" + TAR_USAGE, 2);
  const stripText = a.value('\u0001');
  if (stripText !== undefined && !/^\d+$/.test(stripText)) throw new ShellError(`Invalid number of components to strip: '${stripText}'${TAR_USAGE}`, 2);
  const strip = stripText === undefined ? 0 : Number(stripText);
  const archive = a.value('f') ?? '-';
  const fs = ctx.fs;
  const fatal = (msg: string) => `${msg}\ntar: Error is not recoverable: exiting now`;
  const dir = ctx.resolvePath(a.value('C') ?? '.');
  if (!fs.isDirectory(dir)) throw new ShellError(fatal(`${a.value('C')}: Cannot open: ${fs.exists(dir) ? 'Not a directory' : fs.whyMissing(dir)}`), 2);
  let code = 0;
  const fail = (msg: string) => { ctx.stderr.write(`tar: ${msg}\n`); code = 2; };
  const done = () => { if (code !== 0) ctx.stderr.write('tar: Exiting with failure status due to previous errors\n'); return code; };

  if (a.has('c')) return create();
  return readArchive();

  async function create(): Promise<number> {
    if (a.positional.length === 0) throw new ShellError('Cowardly refusing to create an empty archive' + TAR_USAGE, 2);
    const archivePath = archive === '-' ? null : ctx.resolvePath(archive);
    const verbose = archive === '-' ? ctx.stderr : ctx.stdout;
    const parts: Uint8Array[] = [];
    const linked: { path: string; member: string }[] = []; // files with several names already stored
    const add = (name: string, path: string) => {
      if (path === archivePath) return; // the archive itself, as GNU tar skips it
      let member = name.replace(/^\/+/, '');
      try {
        const st = fs.stat(path, false); // a symbolic link is stored as one, never followed
        if (st.isDirectory) member = member.replace(/\/+$/, '') + '/';
        // a file with several names: the first goes in whole, later ones as links to it
        const first = st.links > 1 && !st.isDirectory ? linked.find(l => fs.sameFile(l.path, path, false))?.member : undefined;
        const type = st.isDirectory ? '5' : first !== undefined ? '1' : st.isSymlink ? '2' : '0';
        const children = st.isDirectory ? fs.list(path) : [];
        const data = type === '0' ? fs.readBytes(path) : new Uint8Array(0);
        if (st.links > 1 && first === undefined && !st.isDirectory) linked.push({ path, member });
        if (member !== '/') {
          parts.push(...tarBlocks({ name: member, type, mode: st.mode, mtime: Math.floor(st.modified / 1000), owner: st.owner, data, link: first ?? st.linkTarget ?? undefined }));
          if (a.has('v')) verbose.write(member + '\n');
        }
        for (const c of children) add(name.replace(/\/+$/, '') + '/' + c, path.replace(/\/+$/, '') + '/' + c);
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        fail(`${name}: Cannot open: ${reason(e)}`);
      }
    };
    let stripped = false;
    for (const f of a.positional) {
      if (f.startsWith('/') && !stripped) { stripped = true; ctx.stderr.write("tar: Removing leading `/' from member names\n"); }
      const path = VirtualFileSystem.normalize(dir, f);
      if (!fs.exists(path, false)) fail(`${f}: Cannot stat: ${fs.whyMissing(path, false)}`);
      else add(f, path);
    }
    parts.push(new Uint8Array(BLOCK * 2));
    let bytes: Uint8Array = concat(parts);
    // GNU tar's default blocking factor: 20 records
    bytes = concat([bytes, new Uint8Array((10240 - bytes.length % 10240) % 10240)]);
    if (a.has('z')) bytes = await deflate('gzip', bytes);
    if (archivePath === null) writeBinary(ctx, bytes);
    else {
      try { fs.writeBytes(archivePath, bytes); } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        throw new ShellError(fatal(`${archive}: Cannot open: ${reason(e)}`), 2);
      }
    }
    return done();
  }

  function readArchive(): number {
    let data: Uint8Array;
    if (archive === '-') data = bytesOf(ctx.stdinText);
    else {
      const path = ctx.resolvePath(archive);
      if (!fs.isFile(path)) throw new ShellError(fatal(`${archive}: Cannot open: ${fs.isDirectory(path) ? 'Is a directory' : fs.whyMissing(path)}`), 2);
      data = fs.readBytes(path);
    }
    if (isGzip(data)) {
      if (!a.has('z') && archive === '-') throw new ShellError(fatal('Archive is compressed. Use -z option'), 2);
      const plain = inflate('gunzip', data, new Budget(ctx.shell.options, 2));
      if (failed(plain)) {
        ctx.stderr.write(`\ngzip: stdin: ${plain.error}\ntar: Child returned status 1\n`);
        throw new ShellError('Error is not recoverable: exiting now', 2);
      }
      data = plain;
    } else if (a.has('z')) {
      ctx.stderr.write('\ngzip: stdin: not in gzip format\ntar: Child returned status 1\n');
      throw new ShellError('Error is not recoverable: exiting now', 2);
    }

    const budget = new Budget(ctx.shell.options, 2);
    const made = new Set<string>(); // symbolic links this extraction created
    const throughMadeLink = (path: string) => {
      for (let p = VirtualFileSystem.getParent(path); p.length > dir.length && p !== '/'; p = VirtualFileSystem.getParent(p)) if (made.has(p) && fs.isSymlink(p)) return true;
      return false;
    };
    const wanted = a.positional.map(p => p.replace(/\/+$/, ''));
    const found = wanted.map(() => false);
    const announced = new Set<string>();
    const info = a.has('O') ? ctx.stderr : ctx.stdout;
    let ugWidth = 19;
    try {
      for (const e of readTar(data)) {
        budget.entry();
        const name = e.name;
        const prefix = tarPrefix(name);
        const rel = name.slice(prefix.length);
        if (wanted.length > 0) {
          const i = wanted.findIndex(w => rel.replace(/\/+$/, '') === w || rel.startsWith(w + '/'));
          if (i < 0) continue;
          found[i] = true;
        }
        if (prefix && !announced.has(prefix)) { announced.add(prefix); ctx.stderr.write(`tar: Removing leading \`${prefix}' from member names\n`); }
        const linkPrefix = e.type === '1' ? tarPrefix(e.link) : '';
        if (linkPrefix && !announced.has('link:' + linkPrefix)) { announced.add('link:' + linkPrefix); ctx.stderr.write(`tar: Removing leading \`${linkPrefix}' from hard link targets\n`); }
        const isFile = isFileType(e.type);
        if (a.has('t')) {
          if (!a.has('v')) { ctx.stdout.write(name + '\n'); continue; }
          const kind = e.type === '5' ? 'd' : e.type === '2' ? 'l' : e.type === '1' ? 'h' : e.type === '3' ? 'c' : e.type === '4' ? 'b' : e.type === '6' ? 'p' : '-';
          const ug = `${e.uname || e.uid}/${e.gname || e.gid}`;
          const size = String(isFile ? e.size : 0);
          ugWidth = Math.max(ugWidth, ug.length + 1 + size.length); // grows like GNU tar's, so later columns stay aligned
          const link = e.type === '2' ? ` -> ${e.link}` : e.type === '1' ? ` link to ${e.link.slice(linkPrefix.length)}` : '';
          ctx.stdout.write(`${kind}${VirtualFileSystem.formatMode(false, e.mode).slice(1)} ${ug}${size.padStart(ugWidth - ug.length)} ${stamp(e.mtime * 1000)} ${name}${link}\n`);
          continue;
        }
        if (prefix.split('/').includes('..')) { fail(`${name}: Member name contains '..'`); continue; }
        const parts = nonEmpty(rel.split('/'));
        if (parts.length <= strip) continue;
        const target = under(dir, parts.slice(strip));
        try {
          if (a.has('O')) {
            if (!isFile) continue;
            if (a.has('v')) info.write(name + '\n');
            budget.charge(e.data.length);
            writeBinary(ctx, e.data);
            continue;
          }
          if (a.has('v')) info.write(name + '\n');
          // like GNU tar's placeholders for links: nothing lands under (or links to something under) a symbolic link this
          // archive made, so a crafted member can't use one to write outside the target directory
          if (throughMadeLink(target)) throw new ShellError(`${target}: Not a directory`);
          if (e.type !== '5' && fs.exists(target, false) && !fs.isDirectory(target, false)) fs.delete(target); // a member replaces what is there, never writes through a link
          if (e.type === '5') fs.mkdir(target, true); // shortcut: the archived directory mode isn't restored, so read-only dirs can't block their own contents
          else if (isFile) {
            budget.charge(e.data.length);
            fs.mkdir(VirtualFileSystem.getParent(target), true);
            fs.writeBytes(target, e.data);
            fs.chmod(target, e.mode & 0o777);
            fs.setModifiedTime(target, e.mtime * 1000);
          } else if (e.type === '1') {
            const linkParts = nonEmpty(e.link.slice(linkPrefix.length).split('/'));
            const linkTarget = under(dir, linkParts.slice(strip));
            // only to a file this archive (or the target dir) already holds, never through a link it made
            if (linkParts.length <= strip || !fs.exists(linkTarget, false) || fs.isDirectory(linkTarget, false) || linkTarget === target || throughMadeLink(linkTarget)) {
              fail(`${name}: Cannot hard link to ‘${linkParts.join('/')}’: No such file or directory`);
              continue;
            }
            fs.mkdir(VirtualFileSystem.getParent(target), true);
            fs.createHardLink(linkTarget, target);
          } else if (e.type === '2') {
            fs.mkdir(VirtualFileSystem.getParent(target), true);
            fs.createSymlink(target, e.link);
            made.add(target);
            fs.setModifiedTime(target, e.mtime * 1000, false);
          } else fail(`${name}: Cannot mknod: Operation not permitted`);
        } catch (ex) {
          if (!(ex instanceof ShellError) || ex instanceof ExtractLimit) throw ex;
          fail(`${name}: Cannot open: ${reason(ex)}`);
        }
      }
    } catch (ex) {
      if (!(ex instanceof TarFormatError)) throw ex;
      fail(ex.message === 'not a tar archive' ? 'This does not look like a tar archive' : 'Unexpected EOF in archive');
    }
    wanted.forEach((_, i) => { if (!found[i]) fail(`${a.positional[i]}: Not found in archive`); });
    return done();
  }
}

// ---------- gzip ----------

/** Header plus trailer bytes of a gzip member, which gzip leaves out of its ratio. */
function gzipOverhead(d: Uint8Array): number {
  const flags = d.length > 3 ? d[3] : 0;
  let i = 10;
  if (flags & 4 && i + 2 <= d.length) i += 2 + (d[i] | d[i + 1] << 8);
  const skipZ = () => { const z = d.indexOf(0, Math.min(i, d.length)); i = z + 1; };
  if (flags & 8) skipZ();
  if (flags & 16 && i > 0) skipZ();
  if (flags & 2) i += 2;
  return Math.max(i, 10) + 8;
}

const ratio = (compressed: number, uncompressed: number) => uncompressed === 0 ? '0.0' : ((uncompressed - compressed) * 100 / uncompressed).toFixed(1);

async function gzip(ctx: CommandContext): Promise<number> {
  try { return await gzipRun(ctx); } catch (e) {
    if (!(e instanceof ExtractLimit)) throw e;
    ctx.stderr.write(`gzip: ${e.message}\n`);
    return e.exitCode;
  }
}

async function gzipRun(ctx: CommandContext): Promise<number> {
  const a = Args.parse(ctx.args, 'cdkflvqtn123456789', '',
    'stdout=c,to-stdout=c,decompress=d,uncompress=d,keep=k,force=f,list=l,verbose=v,quiet=q,test=t,no-name=n,fast=1,best=9');
  const decompress = ctx.name !== 'gzip' || a.has('d') || a.has('t') || a.has('l');
  const toStdout = ctx.name === 'zcat' || a.has('c');
  const fs = ctx.fs;
  const budget = new Budget(ctx.shell.options, 1);
  let code = 0;
  const error = (msg: string) => { ctx.stderr.write(`gzip: ${msg}\n`); code = 1; };
  const warn = (msg: string) => { if (!a.has('q')) ctx.stderr.write(`gzip: ${msg}\n`); if (code === 0) code = 2; };
  const files = a.positional.length === 0 ? ['-'] : a.positional;
  let totalC = 0, totalU = 0, totalH = 0, header = false;

  // writes the result next to the source with its mode and time, and removes the source unless -k
  const replace = (from: string, to: string, bytes: Uint8Array, shown: string) => {
    try {
      const st = fs.stat(from);
      fs.writeBytes(to, bytes);
      fs.chmod(to, st.mode);
      fs.setModifiedTime(to, st.modified);
      if (!a.has('k')) fs.delete(from);
      return true;
    } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      error(`${shown}: ${reason(e)}`);
      return false;
    }
  };

  for (const arg of files) {
    let f = arg;
    let path = ctx.resolvePath(f);
    if (decompress && f !== '-' && !fs.exists(path) && !f.endsWith('.gz')) { f += '.gz'; path += '.gz'; }
    let data: Uint8Array;
    if (f === '-') data = bytesOf(ctx.stdinText);
    else if (fs.isDirectory(path)) { warn(`${f} is a directory -- ignored`); continue; }
    else if (!fs.isFile(path)) { error(`${f}: ${fs.whyMissing(path)}`); continue; }
    else {
      try { data = fs.readBytes(path); } catch (e) { if (!(e instanceof ShellError)) throw e; error(`${f}: ${reason(e)}`); continue; }
    }
    const shown = f === '-' ? 'stdin' : f;

    if (!decompress) {
      const packed = await deflate('gzip', data);
      if (f === '-' || toStdout) { writeBinary(ctx, packed); continue; }
      if (f.endsWith('.gz')) { if (!a.has('q')) ctx.stderr.write(`gzip: ${f} already has .gz suffix -- unchanged\n`); continue; }
      if (fs.exists(path + '.gz') && !a.has('f')) { warn(`${f}.gz already exists;\tnot overwritten`); continue; }
      if (replace(path, path + '.gz', packed, f) && a.has('v'))
        ctx.stderr.write(`${f}:\t${ratio(packed.length - gzipOverhead(packed), data.length)}% -- ${a.has('k') ? 'created' : 'replaced with'} ${f}.gz\n`);
      continue;
    }

    let outName: string | null = null;
    if (f !== '-' && !toStdout && !a.has('t') && !a.has('l')) {
      const suffix = ['.gz', '-gz', '.z', '_z', '.tgz'].find(x => f.endsWith(x) && f.length > x.length);
      if (!suffix) { warn(`${f}: unknown suffix -- ignored`); continue; }
      outName = f.slice(0, -suffix.length) + (suffix === '.tgz' ? '.tar' : '');
    }
    if (!isGzip(data)) {
      if (a.has('f') && toStdout) { writeBinary(ctx, data); continue; } // gzip -dcf passes plain data through
      ctx.stderr.write(`\ngzip: ${shown}: not in gzip format\n`);
      code = 1;
      continue;
    }
    if (a.has('l')) {
      const c = data.length;
      const u = data.length >= 4 ? new DataView(data.buffer, data.byteOffset, data.byteLength).getUint32(data.length - 4, true) : 0;
      const h = gzipOverhead(data);
      totalC += c; totalU += u; totalH += h;
      const name = f === '-' ? 'stdout' : f.endsWith('.gz') ? f.slice(0, -3) : f;
      if (!header) { header = true; ctx.stdout.write('         compressed        uncompressed  ratio uncompressed_name\n'); }
      ctx.stdout.write(`${pad(c, 19)} ${pad(u, 19)} ${pad(ratio(c - h, u), 5)}% ${name}\n`);
      continue;
    }
    const plain = inflate('gunzip', data, budget);
    if (failed(plain)) { ctx.stderr.write(`\ngzip: ${shown}: ${plain.error}\n`); code = 1; continue; }
    if (a.has('t')) continue;
    if (outName === null) { writeBinary(ctx, plain); continue; }
    const outPath = ctx.resolvePath(outName);
    if (fs.exists(outPath) && !a.has('f')) { warn(`${outName} already exists;\tnot overwritten`); continue; }
    if (replace(path, outPath, plain, outName) && a.has('v'))
      ctx.stderr.write(`${f}:\t${ratio(data.length - gzipOverhead(data), plain.length)}% -- replaced with ${outName}\n`);
  }
  if (header && files.length > 1) ctx.stdout.write(`${pad(totalC, 19)} ${pad(totalU, 19)} ${pad(ratio(totalC - totalH, totalU), 5)}% (totals)\n`);
  return code;
}

// ---------- zip ----------

async function zip(ctx: CommandContext): Promise<number> {
  const a = Args.parse(ctx.args, 'rq0123456789', '', 'recurse-paths=r,quiet=q');
  const fs = ctx.fs;
  if (a.positional.length === 0) throw new ShellError('\nzip error: Nothing to do! (no archive given)', 12);
  let archive = a.positional[0];
  if (archive !== '-' && !VirtualFileSystem.getName(archive).includes('.')) archive += '.zip';
  const archivePath = archive === '-' ? null : ctx.resolvePath(archive);
  const info = archivePath === null ? ctx.stderr : ctx.stdout;
  let entries: ZipEntry[] = [];
  if (archivePath !== null && fs.isFile(archivePath)) {
    try { entries = readZip(fs.readBytes(archivePath)); } catch (e) {
      if (!(e instanceof ZipFormatError)) throw e;
      throw new ShellError(`\nzip error: Zip file structure invalid (${archive})`, 3);
    }
  }
  let added = 0;
  const walking = new Set<string>(); // real paths of the directories being added
  const add = async (name: string, path: string): Promise<void> => {
    if (path === archivePath || !fs.exists(path)) return; // a dangling link has nothing to store
    ctx.checkTime(); // following links, -r can walk far more than the tree
    const st = fs.stat(path);
    const entryName = name.split('/').filter(p => p !== '' && p !== '.').join('/') + (st.isDirectory ? '/' : '');
    if (entryName !== '/') {
      const data = st.isDirectory ? new Uint8Array(0) : fs.readBytes(path);
      const deflated = data.length === 0 ? new Uint8Array(0) : await deflate('deflate-raw', data);
      const stored = a.has('0') || deflated.length === 0 || deflated.length >= data.length;
      const old = entries.findIndex(e => e.name === entryName);
      if (old >= 0) entries.splice(old, 1);
      const [time, date] = dosTime(st.modified);
      entries.push({
        name: entryName, method: stored ? 0 : 8, flags: 0, crc: crc32(data), usize: data.length, time, date,
        ext: ((((st.isDirectory ? 0o40000 : 0o100000) | (st.mode & 0o7777)) << 16) | (st.isDirectory ? 0x10 : 0)) >>> 0,
        data: stored ? data : deflated,
      });
      added++;
      if (!a.has('q')) {
        const pct = stored ? 0 : (200 * (data.length - deflated.length) / data.length + 1) >> 1;
        info.write(`${old >= 0 ? 'updating' : '  adding'}: ${entryName} (${stored ? 'stored 0' : `deflated ${Math.floor(pct)}`}%)\n`);
      }
    }
    if (st.isDirectory && (a.has('r') || entryName === '/')) {
      // zip follows links to directories: one leading back up the tree would never end
      const real = fs.realPath(path);
      if (real === null || walking.has(real)) return;
      walking.add(real);
      for (const child of fs.list(path)) await add(name.replace(/\/+$/, '') + '/' + child, path.replace(/\/+$/, '') + '/' + child);
      walking.delete(real);
    }
  };
  const names = a.positional.slice(1);
  for (const f of names) if (!fs.exists(ctx.resolvePath(f)) && !a.has('q')) info.write(`\tzip warning: name not matched: ${f}\n`);
  for (const f of names) if (fs.exists(ctx.resolvePath(f))) await add(f, ctx.resolvePath(f));
  if (added === 0) {
    const hint = a.has('r') ? `try: zip -r ${archive} . -i ${names.join(' ')}` : archive;
    info.write(`\nzip error: Nothing to do! (${hint})\n`);
    return 12;
  }
  const bytes = writeZip(entries);
  if (archivePath === null) writeBinary(ctx, bytes);
  else fs.writeBytes(archivePath, bytes);
  return 0;
}

// ---------- unzip ----------

function unzip(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, 'lopqn', 'd');
  const fs = ctx.fs;
  if (a.positional.length === 0) {
    ctx.stdout.write('UnZip 6.00 of 20 April 2009, by Debian. Original by Info-ZIP.\n\nUsage: unzip [-Z] [-opts[modifiers]] file[.zip] [list] [-x xlist] [-d exdir]\n');
    return 0;
  }
  const name = a.positional[0];
  const path = [name, name + '.zip', name + '.ZIP'].map(p => ctx.resolvePath(p)).find(p => fs.isFile(p));
  if (path === undefined) {
    ctx.stderr.write(`unzip:  cannot find or open ${name}, ${name}.zip or ${name}.ZIP.\n`);
    return 9;
  }
  const quiet = a.has('q'), pipe = a.has('p');
  let entries: ZipEntry[];
  try { entries = readZip(fs.readBytes(path)); } catch (e) {
    if (!(e instanceof ZipFormatError)) throw e;
    if (!quiet) ctx.stdout.write(`Archive:  ${name}\n`);
    ctx.stderr.write('  End-of-central-directory signature not found.  Either this file is not\n  a zipfile, or it constitutes one disk of a multi-part archive.  In the\n  latter case the central directory and zipfile comment will be found on\n  the last disk(s) of this archive.\n');
    ctx.stderr.write(`unzip:  cannot find zipfile directory in one of ${name} or\n        ${name}.zip, and cannot find ${name}.ZIP, period.\n`);
    return 9;
  }
  if (!quiet && !pipe) ctx.stdout.write(`Archive:  ${name}\n`);
  const patterns = a.positional.slice(1).map(p => globRegex(p, false, true));
  const found = patterns.map(() => false);
  const selected = (e: ZipEntry) => {
    if (patterns.length === 0) return true;
    let any = false;
    patterns.forEach((re, i) => { if (re.test(e.name)) any = found[i] = true; });
    return any;
  };
  const budget = new Budget(ctx.shell.options, 1);
  let code = 0;
  const inflateEntry = (e: ZipEntry): Uint8Array => {
    if (e.flags & 1 || (e.method !== 0 && e.method !== 8)) throw new ShellError(`${e.name}: unsupported compression method or encryption`, 2);
    let plain: Uint8Array;
    if (e.method === 0) { budget.charge(e.data.length); plain = e.data; }
    else {
      const r = inflate('raw', e.data, budget);
      if (failed(r)) throw new ShellError(`${e.name}: ${r.error}`, 2);
      plain = r;
    }
    if (crc32(plain) !== e.crc) throw new ShellError(`${e.name}: bad CRC`, 2);
    return plain;
  };

  if (a.has('l')) {
    ctx.stdout.write('  Length      Date    Time    Name\n---------  ---------- -----   ----\n');
    let total = 0, count = 0;
    for (const e of entries.filter(selected)) {
      budget.entry();
      total += e.usize;
      count++;
      ctx.stdout.write(`${pad(e.usize, 9)}  ${stamp(fromDos(e.time, e.date))}   ${e.name}\n`);
    }
    ctx.stdout.write(`---------                     -------\n${pad(total, 9)}                     ${count} file${count === 1 ? '' : 's'}\n`);
  } else {
    const destArg = a.value('d');
    const dest = ctx.resolvePath(destArg ?? '.');
    const shownDir = destArg === undefined ? '' : destArg.replace(/\/+$/, '') + '/';
    let none = false;
    for (const e of entries.filter(selected)) {
      budget.entry();
      const entryName = e.name;
      const isDir = entryName.endsWith('/');
      if (pipe) { if (!isDir) writeBinary(ctx, inflateEntry(e)); continue; }
      const parts = entryName.split('/');
      if (parts.includes('..')) { if (!quiet) ctx.stdout.write(`warning:  skipped "../" path component(s) in ${entryName}\n`); code = 1; }
      if (entryName.startsWith('/')) { ctx.stderr.write(`warning:  stripped absolute path spec from ${entryName}\n`); code = 1; }
      const clean = parts.filter(p => p !== '' && p !== '.' && p !== '..');
      if (clean.length === 0) continue;
      const rel = clean.join('/');
      const target = under(dest, clean);
      const shown = shownDir + rel + (isDir ? '/' : '');
      try {
        if (isDir) {
          if (fs.isDirectory(target)) continue;
          fs.mkdir(target, true);
          if (!quiet) ctx.stdout.write(`   creating: ${shown}\n`);
          continue;
        }
        if (fs.exists(target) && !a.has('o')) {
          if (a.has('n') || none) continue;
          // shortcut: no interactive stdin, so the prompt always reads EOF, as unzip does with </dev/null
          ctx.stderr.write(`replace ${shown}? [y]es, [n]o, [A]ll, [N]one, [r]ename:  NULL\n(EOF or read error, treating as "[N]one" ...)\n`);
          none = true;
          code = Math.max(code, 1);
          continue;
        }
        const bytes = inflateEntry(e);
        fs.mkdir(VirtualFileSystem.getParent(target), true);
        fs.writeBytes(target, bytes);
        const mode = (e.ext >>> 16) & 0o777;
        if (mode !== 0) fs.chmod(target, mode);
        fs.setModifiedTime(target, fromDos(e.time, e.date));
        if (!quiet) ctx.stdout.write(`${e.method === 0 ? ' extracting' : '  inflating'}: ${shown.padEnd(22)}  \n`);
      } catch (ex) {
        if (!(ex instanceof ShellError) || ex instanceof ExtractLimit) throw ex;
        ctx.stderr.write(`error:  cannot create ${shown}\n        ${reason(ex)}\n`);
        code = Math.max(code, 1);
      }
    }
  }
  patterns.forEach((_, i) => {
    if (!found[i]) { if (!a.has('l')) ctx.stderr.write(`caution: filename not matched:  ${a.positional[i + 1]}\n`); code = 11; }
  });
  return code;
}

export function registerArchives(shell: Shell): void {
  shell.register('tar', tar, 'Create, list or extract tar archives (c/x/t, -f, -z, -v, -C, -O, --strip-components)');
  shell.register('gzip', gzip, 'Compress or decompress files (-c -d -k -f -l -t -v -1..-9)');
  shell.register('gunzip', gzip, 'Decompress .gz files (gzip -d)');
  shell.register('zcat', gzip, 'Decompress .gz files to stdout (gzip -dc)');
  shell.register('zip', zip, 'Package files into a zip archive (-r, -q)');
  shell.register('unzip', unzip, 'List or extract a zip archive (-l -o -n -d -p -q)');
}
