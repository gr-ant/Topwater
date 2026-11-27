// git: a local version control system over the virtual filesystem (see gitcore.ts for the formats). No network, no
// remotes; the .git directory it writes is a real repository. A port of python/src/topwater/commands/git.py (the
// reference); keep them in step.
import type { Shell } from '../shell.js';
import { ShellError, specSubMessage, type CommandContext } from '../command.js';
import { VirtualFileSystem } from '../vfs.js';
import { decodeBytes, encodeText, bytesToText } from '../bytes.js';
import { zlibSync } from '../compress.js';
import { inflateRaw, OutputLimitError } from '../inflate.js';
import { COMMANDS, type CommandSpec, type SubcommandSpec } from '../spec.generated.js';
import {
  EMPTY_TREE, HEX, MAX_DEPTH, MAX_WALK, SHA, Commit, Config, DateError, Entry, GitError, IgnoreRules, cQuote, canonicalKey, checkPath,
  cmpCp, cmpPath, concat, configBool, countChanges, cpLen, cpSlice, fatal, formatDate, globFull, isBinary, isDigits, ljust,
  lstripChars, mergeLines, msg, myers, objectId, parseDate, parseIdent, parseTree, partition, readIndex, rjust, rstripChars, sortCp,
  sortPaths, splitLines, stripChars, treeBytes, unified, validKey, writeIndex, type TreeEntry, type Values,
} from './gitcore.js';
import { MAX_NAMES, MAX_PACKS, Corrupt, ObjectCache, Pack } from './gitpack.js';
import { posixRegexError, posixToJs } from '../regex.js';

const SPEC = (): CommandSpec => COMMANDS.get('git')!;
const subSpec = (sub: string): SubcommandSpec => SPEC().subcommands!.get(sub)!;
const normalize = (cwd: string, p: string) => VirtualFileSystem.normalize(cwd, p);

const subMsg = (sub: string, key: string, values: Values = {}) => specSubMessage('git', sub, key, values);
const subFatal = (sub: string, key: string, values: Values = {}, code = 128) => new GitError(subMsg(sub, key, values), code);

/** A file as a tree or the index holds it: [mode, blob id]. */
type F = [mode: string, sha: string];
type Files = Map<string, F>;
const same = (x: F | undefined | null, y: F | undefined | null) => (x ?? null) === (y ?? null) || (!!x && !!y && x[0] === y[0] && x[1] === y[1]);
const union = (...xs: Iterable<string>[]) => { const s = new Set<string>(); for (const x of xs) for (const v of x) s.add(v); return s; };
const hasGlob = (s: string) => /[*?[]/.test(s);

// ---------- option parsing ----------

class Opts {
  flags = new Map<string, number>();
  values = new Map<string, string[]>();
  args: string[] = []; // operands before "--"
  paths: string[] = []; // operands after "--"
  dashdash = false;
  has(k: string) { return this.flags.has(k); }
  val(k: string): string | null;
  val(k: string, d: string): string;
  val(k: string, d: string | null = null): string | null { const v = this.values.get(k); return v && v.length ? v[v.length - 1] : d; }
  vals(k: string): string[] { return this.values.get(k) ?? []; }
}

function usageError(sub: string, text: string): GitError {
  return new GitError(text + '\n' + rstripChars(subSpec(sub).help, '\n'), 129);
}

function parse(sub: string, args: string[]): Opts {
  const [flags, valued, longs] = subSpec(sub).options;
  const mapping = new Map<string, string>();
  for (const x of longs.split(',')) if (x) { const [k, , v] = partition(x, '='); mapping.set(k, v); }
  if (['diff', 'log', 'show'].includes(sub)) args = args.filter(a => !/^--color(=.*)?$/s.test(a)); // accepted and ignored (never colored)
  const o = new Opts();
  const add = (k: string, v?: string) => {
    if (v !== undefined) { const l = o.values.get(k) ?? []; l.push(v); o.values.set(k, l); }
    o.flags.set(k, (o.flags.get(k) ?? 0) + 1);
  };
  let i = 0;
  while (i < args.length) {
    const a = args[i++];
    if (a === '--') { o.dashdash = true; o.paths = args.slice(i); break; }
    if (a.startsWith('--')) {
      const [name, eq, inl] = partition(a.slice(2), '=');
      let inline = inl;
      const neg = name.startsWith('no-') && !mapping.has(name) && mapping.has(name.slice(3));
      const key = mapping.get(neg ? name.slice(3) : name);
      if (key === undefined) throw usageError(sub, msg('unknownOption', { option: name }));
      if (neg) { o.flags.delete(key); o.values.delete(key); o.flags.set('!' + key, 1); continue; }
      if (valued.includes(key)) {
        if (!eq) {
          if (i >= args.length) throw new GitError(msg('optionNeedsValue', { option: name }), 129);
          inline = args[i++];
        }
        add(key, inline);
      } else {
        if (eq) throw new GitError(msg('optionTakesNoValue', { option: name }), 129);
        add(key);
      }
      continue;
    }
    if (a.length < 2 || a[0] !== '-') { o.args.push(a); continue; }
    for (let k = 1; k < a.length; k++) {
      const c = a[k];
      if (valued.includes(c)) {
        let v = a.slice(k + 1);
        if (!v) {
          if (i >= args.length) throw new GitError(msg('switchNeedsValue', { option: c }), 129);
          v = args[i++];
        }
        add(c, v);
        break;
      }
      if (!flags.includes(c)) throw usageError(sub, msg('unknownSwitch', { option: c }));
      add(c);
    }
  }
  return o;
}

// ---------- the repository ----------

/** A packed-refs name git could have written: under refs/, the characters loose refs allow, no empty, '.'-led or
 * '..' component and no .lock name, so nothing read from the file can name a path outside refs/. */
const packedNameOk = (name: string) => name.startsWith('refs/') && /^[A-Za-z0-9_./-]+$/.test(name)
  && name.split('/').every(c => c !== '' && !c.startsWith('.') && !c.endsWith('.lock'));

class Repo {
  readonly ctx: CommandContext;
  readonly fs: VirtualFileSystem;
  readonly limit: number;
  private cache = new ObjectCache(); // objects, commits and pack delta bases, under one byte budget
  private packList: Pack[] | null = null;
  private cfg: Config | null = null;
  private ignores = new Map<string, IgnoreRules | null>();
  private excludes: IgnoreRules[] | null = null;
  prefix = ''; // the cwd below the top ("sub/dir/"), for relative paths

  constructor(readonly g: Git, readonly top: string, readonly git: string) {
    this.ctx = g.ctx;
    this.fs = g.ctx.fs;
    this.limit = g.ctx.shell.options.maxStringBytes;
  }

  // --- files under .git, never through a symbolic link ---
  gpath(rel: string): string {
    let p = this.git;
    for (const part of rel.split('/')) {
      if (part === '' || part === '.' || part === '..') throw fatal('invalidPath', { path: rel });
      p += '/' + part;
      if (this.fs.isSymlink(p)) throw fatal('symlinkInGitDir', { path: p });
    }
    return p;
  }
  gread(rel: string): Uint8Array | null {
    const p = this.gpath(rel);
    if (!this.fs.isFile(p, false)) return null;
    if (this.fs.stat(p).size > this.limit) throw fatal('tooLarge', { what: p });
    return this.fs.readBytes(p);
  }
  gtext(rel: string): string | null { const b = this.gread(rel); return b === null ? null : bytesToText(b); }
  gwrite(rel: string, data: Uint8Array | string): void {
    const p = this.gpath(rel);
    const d = p.slice(0, p.lastIndexOf('/'));
    if (!this.fs.isDirectory(d, false)) this.fs.mkdir(d, true);
    this.fs.writeBytes(p, typeof data === 'string' ? encodeText(data) : data);
  }
  gremove(rel: string): void { const p = this.gpath(rel); if (this.fs.exists(p, false)) this.fs.delete(p); }
  gexists(rel: string): boolean { return this.fs.exists(this.gpath(rel), false); }

  // --- objects ---
  obj(sha: string): [string, Uint8Array] {
    const known = this.cache.get<[string, Uint8Array]>(sha);
    if (known) return known;
    if (this.packs().some(p => p.offsets.has(sha))) return this.packed(sha);
    const raw = SHA.test(sha) ? this.gread(`objects/${sha.slice(0, 2)}/${sha.slice(2)}`) : null;
    if (raw === null) return this.packed(sha);
    let data: Uint8Array;
    try {
      if (raw.length < 2 || (raw[0] & 0x0f) !== 8 || raw[0] >> 4 > 7 || raw[1] & 0x20 || ((raw[0] << 8) | raw[1]) % 31 !== 0) throw new Error('zlib header');
      data = inflateRaw(raw.subarray(2), this.limit + 64);
    } catch (e) {
      if (e instanceof OutputLimitError) throw fatal('tooLarge', { what: `object ${sha}` });
      throw fatal('corruptObject', { object: sha });
    }
    const nul = data.indexOf(0);
    const head = nul > 0 ? String.fromCharCode(...data.subarray(0, nul)).split(' ') : [];
    if (head.length !== 2 || !['blob', 'tree', 'commit', 'tag'].includes(head[0]) || !isDigits(head[1]) || Number(head[1]) !== data.length - nul - 1)
      throw fatal('corruptObject', { object: sha });
    const result: [string, Uint8Array] = [head[0], data.subarray(nul + 1)];
    if (objectId(...result) !== sha) throw fatal('corruptObject', { object: sha });
    this.cache.put(sha, result, result[1].length);
    return result;
  }
  /** The packs under objects/pack that have an index, loaded and verified on first use. */
  packs(): Pack[] {
    if (this.packList === null) {
      const list: Pack[] = [];
      const d = 'objects/pack';
      const names = this.fs.isDirectory(this.gpath(d), false) ? this.fs.list(this.gpath(d)).filter(n => n.endsWith('.idx')).sort(cmpPath) : [];
      if (names.length > MAX_PACKS) throw fatal('tooLarge', { what: this.gpath(d) });
      for (const name of names) {
        const pack = this.gread(`${d}/${name.slice(0, -4)}.pack`);
        // like git, an index without its pack is skipped
        if (pack === null) continue;
        list.push(new Pack(this.gpath(`${d}/${name.slice(0, -4)}.pack`), this.gread(`${d}/${name}`) ?? new Uint8Array(0), pack, this.limit, () => this.ctx.checkTime(), this.cache));
        if (list.reduce((n, p) => n + p.names.length, 0) > MAX_NAMES) throw fatal('tooLarge', { what: this.gpath(d) });
      }
      this.packList = list;
    }
    return this.packList;
  }
  private packed(sha: string): [string, Uint8Array] {
    for (const pack of this.packs()) {
      if (!pack.offsets.has(sha)) continue;
      let result: [string, Uint8Array];
      try {
        result = pack.read(sha);
      } catch (e) {
        if (e instanceof Corrupt) throw fatal('corruptObject', { object: sha });
        throw e;
      }
      if (objectId(...result) !== sha) throw fatal('corruptObject', { object: sha });
      this.cache.put(sha, result, result[1].length);
      return result;
    }
    throw fatal('missingObject', { object: sha });
  }
  has(sha: string): boolean { return this.packs().some(p => p.offsets.has(sha)) || this.gexists(`objects/${sha.slice(0, 2)}/${sha.slice(2)}`); }
  put(kind: string, data: Uint8Array | string): string {
    const bytes = typeof data === 'string' ? encodeText(data) : data;
    const sha = objectId(kind, bytes);
    // synchronous, so an object is on disk before any ref, index or log that names it
    if (!this.has(sha)) this.gwrite(`objects/${sha.slice(0, 2)}/${sha.slice(2)}`, zlibSync(concat([encodeText(`${kind} ${bytes.length}\0`), bytes])));
    return sha;
  }
  typed(sha: string, kind: string): Uint8Array {
    const [k, data] = this.obj(sha);
    if (k !== kind) throw fatal('wrongType', { object: sha, kind });
    return data;
  }
  commit(sha: string): Commit {
    const c = this.cache.get<Commit>('commit:' + sha);
    if (c) return c;
    const data = decodeBytes(this.typed(sha, 'commit'));
    const [head, , message] = partition(data, '\n\n');
    let tree: string | null = null, author = '', committer = '';
    const parents: string[] = [];
    for (const line of head.split('\n')) {
      const [k, , v] = partition(line, ' ');
      if (k === 'tree') tree = v;
      else if (k === 'parent') parents.push(v);
      else if (k === 'author') author = v;
      else if (k === 'committer') committer = v;
    }
    if (!tree || !SHA.test(tree) || parents.some(p => !SHA.test(p))) throw fatal('corruptObject', { object: sha });
    const made = new Commit(sha, tree, parents, author, committer, message);
    this.cache.put('commit:' + sha, made, data.length + 200);
    return made;
  }
  tree(sha: string): TreeEntry[] { return parseTree(this.typed(sha, 'tree'), sha); }

  /** path -> [mode, sha] for every file and link in a tree. */
  flatten(treeSha: string | null, prefix = '', depth = 0, out: Files = new Map()): Files {
    if (treeSha === null || treeSha === EMPTY_TREE) return out;
    if (depth > MAX_DEPTH) throw fatal('tooDeep', { object: treeSha });
    for (const [mode, name, sha] of this.tree(treeSha)) {
      this.ctx.checkTime();
      if (mode === '040000') this.flatten(sha, prefix + name + '/', depth + 1, out);
      else if (mode !== '160000') out.set(prefix + name, [mode, sha]);
      if (out.size > 1_000_000) throw fatal('tooLarge', { what: `tree ${treeSha}` });
    }
    return out;
  }

  writeTree(files: Files): string {
    type Node = Map<string, Node | F>;
    const root: Node = new Map();
    for (const [path, f] of files) {
      let node: Node | F = root;
      const parts = path.split('/');
      for (const p of parts.slice(0, -1)) {
        if (!(node instanceof Map)) break;
        if (!node.has(p)) node.set(p, new Map());
        node = node.get(p)!;
        if (!(node instanceof Map)) throw fatal('invalidPath', { path });
      }
      (node as Node).set(parts[parts.length - 1], f);
    }
    const build = (node: Node): string => {
      const entries: TreeEntry[] = [];
      for (const [name, v] of node) entries.push(v instanceof Map ? ['040000', name, build(v)] : [v[0], name, v[1]]);
      return this.put('tree', treeBytes(entries));
    };
    return build(root);
  }

  // --- refs ---
  /** The object a ref (HEAD, refs/heads/x, ...) points to, following symbolic refs; null when it doesn't exist. */
  readRef(name: string, depth = 0): string | null {
    if (depth > 5) return null;
    if (!/^[A-Za-z0-9_./-]+$/.test(name) || name.includes('..') || name.startsWith('/') || name.endsWith('/')) return null;
    let text = !this.fs.isDirectory(this.gpath(name), false) ? this.gtext(name) : null;
    if (text !== null) {
      text = text.trim();
      if (text.startsWith('ref:')) return this.readRef(text.slice(4).trim(), depth + 1);
      return SHA.test(text) ? text : null;
    }
    return this.packedRefs().get(name) ?? null;
  }
  packedRefs(): Map<string, string> {
    const out = new Map<string, string>();
    for (const line of (this.gtext('packed-refs') ?? '').split('\n')) {
      const parts = line.split(' ');
      if (parts.length === 2 && SHA.test(parts[0]) && packedNameOk(parts[1])) out.set(parts[1], parts[0]);
    }
    return out;
  }
  /** The branch HEAD names (refs/heads/x), or null when detached. */
  headRef(): string | null {
    const text = (this.gtext('HEAD') ?? '').trim();
    if (!text.startsWith('ref:')) return null;
    const target = text.slice(4).trim();
    if (!validHeadTarget(target)) throw fatal('badSymbolicRef', { path: 'HEAD', target });
    return target;
  }
  head(): string | null { return this.readRef('HEAD'); }
  branch(): string | null {
    const r = this.headRef();
    return r && r.startsWith('refs/heads/') ? r.slice('refs/heads/'.length) : r;
  }
  updateRef(name: string, sha: string, log: string | null = null): void {
    if (name === 'HEAD' && this.headRef()) name = this.headRef()!;
    const old = this.readRef(name) ?? '0'.repeat(40);
    this.gwrite(name, sha + '\n');
    if (log !== null) {
      this.reflog(name, old, sha, log);
      if (this.headRef() === name) this.reflog('HEAD', old, sha, log);
    }
  }
  reflog(name: string, old: string, neu: string, message: string): void {
    const who = this.g.ident('committer', false);
    const rel = 'logs/' + name;
    const prev = this.gread(rel) ?? new Uint8Array(0);
    this.gwrite(rel, concat([prev, encodeText(`${old} ${neu} ${who}\t${message}\n`)]));
  }
  /** Points HEAD at a branch (refs/heads/x) or, detached, at a commit. */
  setHead(target: string, log: string | null = null, detach = false): void {
    const old = this.head() ?? '0'.repeat(40);
    this.gwrite('HEAD', detach ? target + '\n' : `ref: ${target}\n`);
    if (log !== null) this.reflog('HEAD', old, detach ? target : (this.readRef(target) ?? '0'.repeat(40)), log);
  }
  deleteRef(name: string): void {
    this.gremove(name);
    this.gremove('logs/' + name);
    const packed = this.gtext('packed-refs');
    if (packed && packed.includes(' ' + name)) {
      const out: string[] = [];
      let skip = false;
      for (const line of packed.split('\n')) {
        if (skip && line.startsWith('^')) continue;
        skip = line.endsWith(' ' + name);
        if (!skip) out.push(line);
      }
      this.gwrite('packed-refs', out.join('\n'));
    }
  }
  /** [name, sha][] under prefix (refs/heads/), loose and packed, sorted by name. */
  refs(prefix: string): [string, string][] {
    const found = new Map([...this.packedRefs()].filter(([k]) => k.startsWith(prefix)));
    const base = this.gpath(rstripChars(prefix, '/'));
    if (this.fs.isDirectory(base, false)) {
      const stack = [rstripChars(prefix, '/')];
      while (stack.length) {
        const rel = stack.pop()!;
        for (const name of this.fs.list(this.gpath(rel))) {
          this.ctx.checkTime();
          const p = rel + '/' + name;
          const full = this.gpath(p);
          if (this.fs.isDirectory(full, false)) stack.push(p);
          else if (this.fs.isFile(full, false)) {
            const v = (this.gtext(p) ?? '').trim();
            if (SHA.test(v)) found.set(p, v);
          }
          if (found.size > 100_000) throw fatal('tooLarge', { what: 'refs' });
        }
      }
    }
    return [...found].sort((a, b) => cmpPath(a[0], b[0]));
  }

  // --- the index ---
  index(): Entry[] {
    const data = this.gread('index');
    return data === null ? [] : readIndex(data, 1_000_000);
  }
  saveIndex(entries: Iterable<Entry>): void { this.gwrite('index', writeIndex([...entries])); }
  stage0(entries?: Entry[]): Map<string, Entry> { return new Map((entries ?? this.index()).filter(e => e.stage === 0).map(e => [e.path, e])); }
  unmerged(entries?: Entry[]): Map<string, Map<number, Entry>> {
    const out = new Map<string, Map<number, Entry>>();
    for (const e of entries ?? this.index()) {
      if (!e.stage) continue;
      if (!out.has(e.path)) out.set(e.path, new Map());
      out.get(e.path)!.set(e.stage, e);
    }
    return out;
  }

  // --- config ---
  config(): Config { return this.cfg ??= new Config(this.gtext('config') ?? ''); }
  saveConfig(): void { this.gwrite('config', this.config().text()); }

  // --- the work tree ---
  wt(rel: string): string { return rel ? this.top.replace(/\/$/, '') + '/' + rel : this.top; }
  /** Whether rel's parent directories are real directories (git never looks through a symbolic link). */
  wtReachable(rel: string): boolean {
    let p = this.top.replace(/\/$/, '');
    const parts = rel.split('/').slice(0, -1);
    for (let k = 0; k < parts.length; k++) {
      p += '/' + parts[k];
      if (k % 32 === 31) this.ctx.checkTime();
      if (!this.fs.isDirectory(p, false)) return false;
    }
    return true;
  }
  /** [mode, data] of a work tree file or link, or null when there is none. */
  wtStat(rel: string): [string, Uint8Array] | null {
    if (!this.wtReachable(rel)) return null;
    const p = this.wt(rel);
    if (this.fs.isSymlink(p)) return ['120000', encodeText(this.fs.readLink(p))];
    if (!this.fs.isFile(p, false)) return null;
    const st = this.fs.stat(p);
    if (st.size > this.limit) throw fatal('tooLarge', { what: rel });
    return [st.mode & 0o100 ? '100755' : '100644', this.fs.readBytes(p)];
  }
  /** [mode, blob id] of a work tree path, or null. */
  wtId(rel: string): F | null { const s = this.wtStat(rel); return s === null ? null : [s[0], objectId('blob', s[1])]; }
  wtWrite(rel: string, mode: string, sha: string): void {
    checkPath(rel);
    const data = this.typed(sha, 'blob');
    let p = this.top.replace(/\/$/, '');
    for (const part of rel.split('/').slice(0, -1)) {
      p += '/' + part;
      if (this.fs.isSymlink(p) || (this.fs.exists(p, false) && !this.fs.isDirectory(p, false))) this.fs.delete(p);
      if (!this.fs.exists(p, false)) this.fs.mkdir(p);
    }
    const full = this.wt(rel);
    if (this.fs.isSymlink(full) || this.fs.isDirectory(full, false)) this.fs.delete(full, true);
    if (mode === '120000') {
      if (this.fs.exists(full, false)) this.fs.delete(full);
      this.fs.createSymlink(full, decodeBytes(data));
      return;
    }
    this.fs.writeBytes(full, data);
    const want = (mode === '100755' ? 0o777 : 0o666) & ~this.fs.umask;
    if ((this.fs.stat(full).mode & 0o777) !== want) this.fs.chmod(full, want);
  }
  wtRemove(rel: string): void {
    if (!this.wtReachable(rel)) return;
    const full = this.wt(rel);
    if (this.fs.exists(full, false) && !this.fs.isDirectory(full, false)) this.fs.delete(full);
    const parts = rel.split('/').slice(0, -1); // empty parent directories go too
    while (parts.length) {
      const d = this.wt(parts.join('/'));
      if (this.g.cwd === d || this.g.cwd.startsWith(d + '/')) break; // the directory git runs in stays
      if (this.fs.isDirectory(d, false) && !this.fs.list(d).length) this.fs.delete(d);
      else break;
      parts.pop();
    }
  }
  entryFor(rel: string, mode: string, sha: string): Entry {
    try {
      const st = this.fs.stat(this.wt(rel), false);
      return new Entry(rel, sha, mode, 0, Math.floor(st.modified / 1000), st.size);
    } catch (e) {
      if (e instanceof ShellError) return new Entry(rel, sha, mode);
      throw e;
    }
  }
  /** Every file and link under base (relative to the top), not descending into .git, symbolic links or nested
   * repositories, which come back as 'dir/' entries. */
  *walk(base = ''): Generator<string> {
    const stack = [base];
    let count = 0;
    while (stack.length) {
      const rel = stack.pop()!;
      let names: string[];
      try { names = sortCp(this.fs.list(this.wt(rel))); } catch (e) { if (e instanceof ShellError) continue; throw e; }
      for (const name of names) {
        if (++count % 256 === 0) this.ctx.checkTime();
        if (name === '.git') continue;
        const r = rel ? rel + '/' + name : name;
        const p = this.wt(r);
        if (this.fs.isDirectory(p, false)) {
          if (this.fs.exists(p + '/.git', false)) yield r + '/';
          else stack.push(r);
        } else yield r;
      }
    }
  }
  ignoreRules(d: string): IgnoreRules | null {
    if (!this.ignores.has(d)) {
      let rules: IgnoreRules | null = null;
      const rel = (d ? d + '/' : '') + '.gitignore';
      const p = this.wt(rel);
      if (this.wtReachable(rel) && this.fs.isFile(p, false) && this.fs.stat(p).size <= 1 << 20) rules = new IgnoreRules(bytesToText(this.fs.readBytes(p)), d, () => this.ctx.checkTime());
      this.ignores.set(d, rules);
    }
    return this.ignores.get(d)!;
  }
  excludeRules(): IgnoreRules[] {
    if (this.excludes === null) {
      const t = this.gtext('info/exclude');
      this.excludes = t ? [new IgnoreRules(t, '', () => this.ctx.checkTime())] : [];
    }
    return this.excludes;
  }
  /** Whether an untracked path is ignored, by .gitignore files from its own directory up, then info/exclude. A path
   * inside an ignored directory is ignored too. */
  ignored(rel: string, isDir = false): boolean {
    const parts = rel.split('/');
    for (let i = 1; i < parts.length; i++) if (this.ignoredHere(parts.slice(0, i).join('/'), true)) return true;
    return this.ignoredHere(rel, isDir);
  }
  private ignoredHere(rel: string, isDir: boolean): boolean {
    const parts = rel.split('/');
    for (let i = parts.length - 1; i >= 0; i--) {
      const r = this.ignoreRules(parts.slice(0, i).join('/'));
      if (r) { const m = r.match(rel, isDir); if (m !== null) return m; }
    }
    for (const r of this.excludeRules()) { const m = r.match(rel, isDir); if (m !== null) return m; }
    return false;
  }

  // --- paths as the user gives and sees them ---
  /** A path the user typed (relative to the cwd) -> relative to the top; error when outside the repository. */
  fromUser(path: string): string {
    const full = normalize(this.g.cwd, path);
    if (full === this.top) return '';
    const top = this.top.replace(/\/$/, '');
    if (!full.startsWith(top + '/')) throw fatal('outsideRepo', { path, top: this.top });
    return full.slice(top.length + 1);
  }
  /** rel (from the top) as seen from the cwd. */
  show(rel: string): string {
    if (!this.prefix) return rel;
    const pre = rstripChars(this.prefix, '/').split('/');
    const trailing = rel.endsWith('/');
    const parts = rstripChars(rel, '/').split('/');
    let k = 0;
    while (k < pre.length && k < parts.length && pre[k] === parts[k]) k++;
    const out = [...Array(pre.length - k).fill('..'), ...parts.slice(k)].join('/');
    return (trailing && out ? out + '/' : out) || './';
  }
}

// ---------- revisions ----------

/** A revision -> object id. Raises badRevision when it names nothing. */
function resolve(repo: Repo, spec: string): string {
  const sha = tryResolve(repo, spec);
  if (sha === null) {
    missingPath(repo, spec);
    throw fatal('badRevision', { rev: spec });
  }
  return sha;
}

/** rev:path where rev exists but path does not gets its own message. */
function missingPath(repo: Repo, spec: string): void {
  const [rev, colon, path] = partition(spec, ':');
  if (colon && rev && tryResolve(repo, rev) !== null) throw fatal('pathNotInRevision', { path, rev });
}

function tryResolve(repo: Repo, spec: string): string | null {
  if (!spec) return null;
  if (spec.includes(':') && !spec.startsWith(':/')) {
    const [rev, , path] = partition(spec, ':');
    if (rev === '') return repo.stage0().get(lstripChars(path, '/'))?.sha ?? null;
    const tree = peel(repo, tryResolve(repo, rev), 'tree');
    if (tree === null) return null;
    if (path === '' || path === '.') return tree;
    let p = path.startsWith('./') ? path.slice(2) : path;
    if (path.startsWith('./') || path.startsWith('../')) {
      try { p = repo.fromUser(path); } catch (e) { if (e instanceof GitError) return null; throw e; }
    }
    return lookupPath(repo, tree, stripChars(p, '/'));
  }
  const m = /^(.*?)((?:[~^]\d*|\^\{\w*\})*)$/.exec(spec);
  if (!m) return null;
  let sha = resolveBase(repo, m[1]);
  if (sha === null) return null;
  for (const op of m[2].match(/~\d*|\^\{\w*\}|\^\d*/g) ?? []) {
    if (op.startsWith('^{')) {
      sha = peel(repo, sha, op.slice(2, -1) || 'any');
    } else if (op[0] === '~') {
      const n = Number(op.slice(1) || 1);
      for (let k = 0; k < n; k++) {
        const c = peel(repo, sha, 'commit');
        if (c === null) return null;
        const ps = repo.commit(c).parents;
        sha = ps.length ? ps[0] : null;
        if (sha === null) return null;
      }
    } else {
      const n = Number(op.slice(1) || 1);
      const c = peel(repo, sha, 'commit');
      if (c === null) return null;
      if (n === 0) { sha = c; continue; }
      const ps = repo.commit(c).parents;
      sha = ps.length >= n ? ps[n - 1] : null;
    }
    if (sha === null) return null;
  }
  return sha;
}

const isUpper = (s: string) => s.toUpperCase() === s && s.toLowerCase() !== s;

function resolveBase(repo: Repo, name: string): string | null {
  if (name === '@' || name === 'HEAD' || name === '') return name ? repo.head() : null;
  const m = /^@\{-(\d+)\}$/.exec(name);
  if (m) {
    const b = previousBranch(repo, Number(m[1]));
    return b ? repo.readRef('refs/heads/' + b) : null;
  }
  const up = /^(.*)@\{(?:u|upstream)\}$/s.exec(name);
  if (up) {
    const u = upstream(repo, upstreamBranch(repo, up[1]));
    return u ? repo.readRef(u[1]) : null;
  }
  for (const r of [name, 'refs/' + name, 'refs/tags/' + name, 'refs/heads/' + name, 'refs/remotes/' + name, 'refs/remotes/' + name + '/HEAD']) {
    if (r === name && !(isUpper(name) || name.startsWith('refs/'))) continue;
    const sha = repo.readRef(r);
    if (sha) return sha;
  }
  if (HEX.test(name)) {
    if (name.length === 40) return repo.has(name) ? name : null;
    return uniquePrefix(repo, name);
  }
  return null;
}

function uniquePrefix(repo: Repo, prefix: string): string | null {
  const d = repo.gpath(`objects/${prefix.slice(0, 2)}`);
  const hits = new Set<string>();
  if (repo.fs.isDirectory(d, false))
    for (const n of repo.fs.list(d)) if ((prefix.slice(0, 2) + n).startsWith(prefix) && n.length === 38) hits.add(prefix.slice(0, 2) + n);
  for (const pack of repo.packs()) for (const h of pack.withPrefix(prefix)) hits.add(h);
  if (hits.size > 1) throw fatal('ambiguousPrefix', { prefix });
  return hits.size ? [...hits][0] : null;
}

/** [HEAD is still there, what to call it] for a detached HEAD: the ref the last checkout named, else the commit it went to,
 * from the HEAD reflog; git says 'detached at' while HEAD has not moved since and 'detached from' after. */
function detachedName(repo: Repo): [boolean, string] {
  const head = repo.head() ?? '';
  const lines = (repo.gtext('logs/HEAD') ?? '').split('\n');
  for (let i = lines.length - 1; i >= 0; i--) {
    const m = /^[0-9a-f]{40} ([0-9a-f]{40}) [^\t]*\tcheckout: moving from \S+ to (\S+)$/.exec(lines[i]);
    if (!m) continue;
    for (const ref of [m[2], 'refs/tags/' + m[2], 'refs/heads/' + m[2], 'refs/remotes/' + m[2]]) {
      const sha = ref.startsWith('refs/') ? repo.readRef(ref) : null;
      if (sha) return [peel(repo, sha, 'commit') === head, ref.replace(/^refs\/(heads|tags|remotes)\//, '')];
    }
    return [m[1] === head, abbrev(m[1])];
  }
  return [true, abbrev(head)];
}

function previousBranch(repo: Repo, n: number): string | null {
  const lines = (repo.gtext('logs/HEAD') ?? '').split('\n');
  let seen = 0;
  for (let i = lines.length - 1; i >= 0; i--) {
    const m = /\tcheckout: moving from (\S+) to (\S+)$/.exec(lines[i]);
    if (m && ++seen === n) return m[1];
  }
  return null;
}

/** Follows tags (and commit -> tree) until an object of kind want ('commit', 'tree', 'any' = not a tag). */
function peel(repo: Repo, sha: string | null, want: string): string | null {
  for (let k = 0; k < 16; k++) {
    if (sha === null) return null;
    const [kind, data] = repo.obj(sha);
    if (kind === want || (want === 'any' && kind !== 'tag')) return sha;
    if (want === 'tag') return null;
    if (kind === 'tag') {
      const m = /^object ([0-9a-f]{40})/.exec(String.fromCharCode(...data.subarray(0, 47)));
      sha = m ? m[1] : null;
    } else if (kind === 'commit' && want === 'tree') sha = repo.commit(sha).tree;
    else return null;
  }
  return null;
}

function lookupPath(repo: Repo, tree: string, path: string): string | null {
  let sha = tree;
  for (const part of path.split('/').filter(Boolean)) {
    if (repo.obj(sha)[0] !== 'tree') return null;
    const found = repo.tree(sha).find(([, n]) => n === part);
    if (!found) return null;
    sha = found[2];
  }
  return sha;
}

function commitOf(repo: Repo, spec: string): string {
  const sha = peel(repo, resolve(repo, spec), 'commit');
  if (sha === null) throw fatal('notCommit', { rev: spec });
  return sha;
}

const treeFiles = (repo: Repo, commit: string | null | undefined): Files => commit ? repo.flatten(repo.commit(commit).tree) : new Map();

function ancestors(repo: Repo, start: Iterable<string>, check: () => void): Set<string> {
  const seen = new Set<string>(), stack = [...start];
  while (stack.length) {
    const c = stack.pop()!;
    if (seen.has(c)) continue;
    seen.add(c);
    if (seen.size > MAX_WALK) throw fatal('tooLarge', { what: 'history' });
    if (seen.size % 256 === 0) check();
    stack.push(...repo.commit(c).parents);
  }
  return seen;
}

/** A best common ancestor: a shared ancestor that is not itself an ancestor of another shared one (newest if several). */
function mergeBase(repo: Repo, a: string, b: string, check: () => void): string | null {
  const bs = ancestors(repo, [b], check);
  const common = [...ancestors(repo, [a], check)].filter(c => bs.has(c));
  if (!common.length) return null;
  const below = ancestors(repo, common.flatMap(c => repo.commit(c).parents), check);
  const best = common.filter(c => !below.has(c)).sort();
  return best.length ? walkOrder(repo, best, check)[0] : null;
}

/** Commits reachable from starts, newest commit date first (ties: first seen first), each once. */
/** [name to show, ref] of a branch's upstream, from branch.<name>.remote and .merge (as git clone sets them). */
function upstream(repo: Repo, branch: string): [string, string] | null {
  const cfg = repo.config();
  const remote = cfg.get(`branch.${branch}.remote`), merge = cfg.get(`branch.${branch}.merge`);
  if (!remote || !merge || !merge.startsWith('refs/heads/')) return null;
  if (remote === '.') return [merge.slice(11), merge];
  // shortcut: assumes the default fetch refspec (refs/remotes/<remote>/*) that clone writes; read remote.<name>.fetch if others show up
  return [`${remote}/${merge.slice(11)}`, `refs/remotes/${remote}/${merge.slice(11)}`];
}

/** The branch <name>@{u} asks about (HEAD and '' mean the current one); refuses as git does when there is none. */
function upstreamBranch(repo: Repo, name: string): string {
  let b: string;
  if (name === '' || name === 'HEAD' || name === '@') {
    const cur = repo.branch();
    if (cur === null) throw fatal('headNotBranch');
    b = cur;
  } else {
    b = name;
    if (repo.readRef('refs/heads/' + b) === null) throw fatal('noSuchBranch', { name: b });
  }
  if (upstream(repo, b) === null) throw fatal('noUpstream', { branch: b });
  return b;
}

/** [upstream name, commits ahead, commits behind, upstream gone] for a branch with an upstream. */
function tracking(repo: Repo, branch: string, check: () => void): [string, number, number, boolean] | null {
  const up = upstream(repo, branch);
  const ours = repo.readRef('refs/heads/' + branch);
  if (up === null || ours === null) return null;
  const theirs = repo.readRef(up[1]);
  if (theirs === null) return [up[0], 0, 0, true];
  const a = ancestors(repo, [ours], check), b = ancestors(repo, [theirs], check);
  let ahead = 0, behind = 0;
  for (const x of a) if (!b.has(x)) ahead++;
  for (const x of b) if (!a.has(x)) behind++;
  return [up[0], ahead, behind, false];
}

/** The 'Your branch is ...' lines git status and checkout print. */
function trackingText(repo: Repo, branch: string, check: () => void): string | null {
  const t = tracking(repo, branch, check);
  if (t === null) return null;
  const [name, ahead, behind, gone] = t;
  const M = (key: string, v: Values = {}) => subMsg('status', key, v);
  const count = (n: number) => n === 1 ? M('oneCommit') : M('commits', { n: String(n) });
  if (gone) return M('upstreamGone', { upstream: name });
  if (ahead && behind) return M('diverged', { upstream: name, ahead: String(ahead), behind: String(behind) });
  if (ahead) return M('ahead', { upstream: name, count: count(ahead) });
  if (behind) return M('behind', { upstream: name, count: count(behind) });
  return M('upToDate', { upstream: name });
}

/** [upstream name, 'ahead 1, behind 2' or 'gone' or ''] for status -sb and branch -vv. */
function trackingBrief(repo: Repo, branch: string, check: () => void): [string, string] | null {
  const t = tracking(repo, branch, check);
  if (t === null) return null;
  const [name, ahead, behind, gone] = t;
  const M = (key: string, v: Values = {}) => subMsg('status', key, v);
  if (gone) return [name, M('trackGone')];
  return [name, [...(ahead ? [M('trackAhead', { n: String(ahead) })] : []), ...(behind ? [M('trackBehind', { n: String(behind) })] : [])].join(', ')];
}

/** [name below refs/remotes/, commit, symbolic target below refs/remotes/ or null] sorted by name. */
function remoteBranches(repo: Repo): [string, string | null, string | null][] {
  const rows: [string, string | null, string | null][] = repo.refs('refs/remotes/').map(([name, sha]) => [name.slice(13), sha, null]);
  const base = 'refs/remotes';
  if (repo.fs.isDirectory(repo.gpath(base), false)) {
    for (const remote of repo.fs.list(repo.gpath(base))) {
      const rel = `${base}/${remote}/HEAD`;
      if (repo.fs.isDirectory(repo.gpath(`${base}/${remote}`), false) && repo.fs.isFile(repo.gpath(rel), false)) {
        const text = (repo.gtext(rel) ?? '').trim();
        if (text.startsWith('ref: refs/remotes/')) rows.push([`${remote}/HEAD`, repo.readRef(rel), text.slice(18)]);
      }
    }
  }
  return rows.sort((x, y) => cmpPath(x[0], y[0]));
}

function walkOrder(repo: Repo, starts: string[], check: () => void, limitSet: Set<string> | null = null): string[] {
  type Item = [number, number, string];
  const heap: Item[] = [];
  const less = (x: Item, y: Item) => x[0] !== y[0] ? x[0] < y[0] : x[1] < y[1];
  const push = (it: Item) => {
    heap.push(it);
    let i = heap.length - 1;
    while (i > 0) { const p = (i - 1) >> 1; if (!less(heap[i], heap[p])) break; [heap[i], heap[p]] = [heap[p], heap[i]]; i = p; }
  };
  const pop = (): Item => {
    const top = heap[0], last = heap.pop()!;
    if (heap.length) {
      heap[0] = last;
      let i = 0;
      for (;;) {
        const l = 2 * i + 1, r = l + 1;
        let m = i;
        if (l < heap.length && less(heap[l], heap[m])) m = l;
        if (r < heap.length && less(heap[r], heap[m])) m = r;
        if (m === i) break;
        [heap[i], heap[m]] = [heap[m], heap[i]];
        i = m;
      }
    }
    return top;
  };
  const seen = new Set<string>(), out: string[] = [];
  let counter = 0;
  for (const s of starts) if (!seen.has(s)) { seen.add(s); push([-commitTime(repo, s), counter++, s]); }
  while (heap.length) {
    const [, , c] = pop();
    out.push(c);
    if (out.length > MAX_WALK) throw fatal('tooLarge', { what: 'history' });
    if (out.length % 256 === 0) check();
    for (const p of repo.commit(c).parents) if (!seen.has(p) && (limitSet === null || limitSet.has(p))) { seen.add(p); push([-commitTime(repo, p), counter++, p]); }
  }
  return out;
}

const commitTime = (repo: Repo, sha: string) => parseIdent(repo.commit(sha).committer)[2];

// ---------- the command ----------

const validHeadTarget = (target: string) => target.startsWith('refs/') && target.split('/').every(p => p !== '' && p !== '.' && p !== '..');

/** A HEAD that names neither a commit nor a ref under refs/ makes the directory not a repository, as in git. */
function validHead(fs: VirtualFileSystem, path: string): boolean {
  if (fs.stat(path).size > 4096) return false;
  const text = bytesToText(fs.readBytes(path)).trim();
  return SHA.test(text) || (text.startsWith('ref:') && validHeadTarget(text.slice(4).trim()));
}

/** Newlines and angle brackets would break the commit header, so they go, as in git. */
const cleanIdent = (s: string) => s.replace(/[<>\n\r]/g, '').trim();

/** One git invocation: the shell context, the directory it works from (after -C) and -c overrides. */
class Git {
  private repoFound: Repo | null = null;
  decor: Map<string, string[]> | null = null;
  constructor(readonly ctx: CommandContext, public cwd: string, readonly overrides: [string, string][]) {}
  out(s: string) { this.ctx.stdout.write(s); }
  err(s: string) { this.ctx.stderr.write(s); }
  get check() { return () => this.ctx.checkTime(); }
  env(name: string): string | null {
    const sh = this.ctx.shell;
    return sh.environment.has(name) && sh.isExported(name) ? sh.environment.get(name)! : null;
  }
  findRepo(): Repo | null {
    if (this.repoFound === null) {
      const fs = this.ctx.fs;
      let d = this.cwd;
      for (;;) {
        const base = d.replace(/\/+$/, '');
        const g = base + '/.git';
        if (fs.isDirectory(g, false) && fs.isFile(g + '/HEAD', false) && validHead(fs, g + '/HEAD')) {
          this.repoFound = new Repo(this, base || '/', g);
          const rel = stripChars(this.cwd.slice(base.length), '/');
          this.repoFound.prefix = rel ? rel + '/' : '';
          break;
        }
        if (fs.isSymlink(g)) throw fatal('symlinkInGitDir', { path: g });
        if (d === '/') break;
        d = d.slice(0, d.lastIndexOf('/')) || '/';
      }
    }
    return this.repoFound;
  }
  repo(): Repo {
    const r = this.findRepo();
    if (r === null) throw fatal('notRepo');
    return r;
  }
  globalConfig(): [string, Config] {
    const path = normalize('/', (this.env('HOME') || '/') + '/.gitconfig');
    const fs = this.ctx.fs;
    const text = fs.isFile(path) && fs.stat(path).size <= 1 << 20 ? bytesToText(fs.readBytes(path)) : '';
    return [path, new Config(text)];
  }
  config(name: string): string | null {
    for (let i = this.overrides.length - 1; i >= 0; i--) if (this.overrides[i][0] === name) return this.overrides[i][1];
    const r = this.findRepo();
    if (r !== null) { const v = r.config().get(name); if (v !== null) return v; }
    return this.globalConfig()[1].get(name);
  }
  now(): number { return this.ctx.shell.now(); }
  ident(kind: string, required = true, name: string | null = null, email: string | null = null, date: string | null = null): string {
    const k = kind.toUpperCase();
    const sh = this.ctx.shell.options;
    name = name || this.env(`GIT_${k}_NAME`) || this.config('user.name') || sh.user;
    email = email !== null ? email : (this.env(`GIT_${k}_EMAIL`) || this.config('user.email') || this.env('EMAIL'));
    const guess = `${sh.user}@${sh.hostname}.(none)`;
    if (email === null) {
      if (required) throw new GitError(msg('identityUnknown', { guess }), 128);
      email = guess;
    }
    const when = date || this.env(`GIT_${k}_DATE`);
    name = cleanIdent(name);
    email = cleanIdent(email);
    if (!name && required) throw new GitError(msg('emptyIdentName', { email }), 128);
    let epoch: number, zone: string;
    if (when) {
      try { [epoch, zone] = parseDate(when, this.now()); } catch (e) {
        if (e instanceof DateError) throw new GitError(msg('badDate', { date: when }), 128);
        throw e;
      }
    } else [epoch, zone] = [Math.floor(this.now() / 1000), '+0000'];
    return `${name} <${email}> ${epoch} ${zone}`;
  }
  /** No message was given: with GIT_EDITOR / core.editor / VISUAL / EDITOR set to true or :, keep the existing message
   * (amend, merge); otherwise say why no editor can open. */
  editorError(existing: string | null = null): string {
    const editor = this.env('GIT_EDITOR') || this.config('core.editor') || this.env('VISUAL') || this.env('EDITOR');
    if (editor && ['true', ':'].includes(editor.trim()) && existing !== null) return existing;
    throw new GitError(subMsg('commit', editor ? 'editorCannotRun' : 'noEditor'), 1);
  }
}

async function git(ctx: CommandContext): Promise<number> {
  const args = [...ctx.args];
  const spec = SPEC();
  let cwd = ctx.shell.cwd;
  const overrides: [string, string][] = [];
  let i = 0;
  while (i < args.length && args[i].startsWith('-')) {
    const a = args[i];
    if (a === '--version' || a === '-v') { ctx.stdout.write(spec.version + '\n'); return 0; }
    if (a === '--help' || a === '-h') { ctx.stdout.write(spec.help!); return 0; }
    if ((a === '-C' || a === '-c') && i + 1 >= args.length) { ctx.stderr.write(msg('globalNeedsValue', { option: a }) + '\n'); return 129; }
    if (a === '-C') {
      const path = args[i + 1];
      if (path) {
        const target = normalize(cwd, path);
        if (!ctx.fs.isDirectory(target)) { ctx.stderr.write(msg('cannotChdir', { path }) + '\n'); return 128; }
        cwd = target;
      }
      i += 2;
    } else if (a === '-c') {
      const [k, eq, v] = partition(args[i + 1], '=');
      if (!validKey(k)) { ctx.stderr.write(msg('badConfigKey', { key: k }) + '\n'); return 129; }
      overrides.push([canonicalKey(k), eq ? v : 'true']);
      i += 2;
    } else if (['--no-pager', '-P', '--paginate', '-p', '--no-replace-objects', '--literal-pathspecs', '--no-optional-locks'].includes(a)) i++;
    else { ctx.stderr.write(msg('unknownGlobalOption', { option: a }) + '\n' + msg('globalUsage') + '\n'); return 129; }
  }
  if (i >= args.length) { ctx.stdout.write(spec.help!); return 1; }
  const sub = args[i], rest = args.slice(i + 1);
  let s = spec.subcommands!.get(sub);
  const dd = rest.indexOf('--');
  if (s && (dd < 0 ? rest : rest.slice(0, dd)).includes('--help')) { ctx.stdout.write(s.help); return 0; }
  if (sub === 'version') { ctx.stdout.write(spec.version + '\n'); return 0; }
  if (sub === 'help') {
    const topic = rest.find(a => !a.startsWith('-'));
    if (topic === undefined) { ctx.stdout.write(spec.help!); return 0; }
    s = spec.subcommands!.get(topic);
    if (!s) { ctx.stderr.write(msg('noHelp', { name: topic }) + '\n'); return 1; }
    ctx.stdout.write(s.help);
    return 0;
  }
  if (!s) { ctx.stderr.write(msg('unknownCommand', { name: sub }) + '\n'); return 1; }
  if (s.unsupported) { ctx.stderr.write(subMsg(sub, 'unsupported', { name: sub }) + '\n'); return 128; }
  if (rest.length === 1 && rest[0] === '-h') { ctx.stdout.write(s.help); return 129; }
  const g = new Git(ctx, cwd, overrides);
  let code: number;
  try {
    code = (await HANDLERS[sub](g, rest)) ?? 0; // bisect run awaits the commands it runs
  } catch (e) {
    if (e instanceof GitError) {
      const text = e.message;
      if (text) ctx.stderr.write(text.endsWith('\n') ? text : text + '\n');
      code = e.code;
    } else if (e instanceof ShellError) {
      ctx.stderr.write(msg('ioError', { error: e.message }) + '\n');
      code = 128;
    } else throw e;
  }
  return code;
}

// ---------- shared pieces: comparing trees, the index and the work tree ----------

/** specs are paths from the top ('' = everything); a spec matches itself, anything below it, or as a glob. */
function pathspecMatch(specs: string[] | null, path: string): boolean {
  if (specs === null) return true;
  for (const s of specs) {
    if (s === '' || path === s || path.startsWith(rstripChars(s, '/') + '/')) return true;
    if (hasGlob(s) && globCached(s)?.test(path)) return true;
  }
  return false;
}

const GLOBS = new Map<string, RegExp | null>();
function globCached(pattern: string): RegExp | null {
  let rx = GLOBS.get(pattern);
  if (rx === undefined) {
    rx = globFull(pattern, false); // a malformed class matches nothing
    if (GLOBS.size < 256) GLOBS.set(pattern, rx);
  }
  return rx;
}

/** An agent's --author / --grep pattern (V8 runs a runaway one on its linear engine, see shell.ts). */
function userRegex(pattern: string | null): RegExp | null {
  if (!pattern) return null;
  try { return new RegExp(pattern); } catch { throw fatal('badRegex', { pattern }); }
}

const userSpecs = (repo: Repo, items: string[]): string[] | null => items.length ? items.map(p => repo.fromUser(p)) : null;

type Change = [status: string, old: string | null, neu: string | null];
const kindOf = (m: string) => m === '120000' ? 'l' : 'f';

/** [status, old path, new path][] between two path -> [mode, sha] maps, sorted by path. A removed and an added path with
 * the same contents pair up as a rename ("R"); with load(side, path), so do ones at least half alike ("R075"). */
function diffFiles(a: Files, b: Files, specs: string[] | null = null, load?: (side: string, path: string) => Uint8Array): Change[] {
  const out: Change[] = [];
  for (const p of sortPaths(union(a.keys(), b.keys()))) {
    if (!pathspecMatch(specs, p)) continue;
    const x = a.get(p), y = b.get(p);
    if (!x) out.push(['A', null, p]);
    else if (!y) out.push(['D', p, null]);
    else if (!same(x, y)) out.push([kindOf(x[0]) !== kindOf(y[0]) ? 'T' : 'M', p, p]);
  }
  const added = new Map<string, Change[]>();
  for (const r of out) if (r[0] === 'A') { const k = b.get(r[2]!)!.join(' '); if (!added.has(k)) added.set(k, []); added.get(k)!.push(r); }
  for (const r of out.filter(r => r[0] === 'D')) {
    const cands = added.get(a.get(r[1]!)!.join(' '));
    if (cands && cands.length) {
      const n = cands.shift()!;
      n[0] = 'R'; n[1] = r[1];
      out.splice(out.indexOf(r), 1);
    }
  }
  if (load) {
    const gone = out.filter(r => r[0] === 'D'), neu = out.filter(r => r[0] === 'A');
    if (gone.length && neu.length && gone.length * neu.length <= RENAME_PAIRS) {
      const src = new Map(gone.map(r => [r[1]!, chunkCounts(load('a', r[1]!))]));
      const dst = new Map(neu.map(n => [n[2]!, chunkCounts(load('b', n[2]!))]));
      const scores: [number, Change, Change][] = [];
      for (const r of gone) for (const n of neu) {
        const sc = similarity(src.get(r[1]!)!, dst.get(n[2]!)!);
        if (sc >= 50) scores.push([sc, r, n]);
      }
      scores.sort((x, y) => y[0] - x[0] || cmpPath(x[2][2]!, y[2][2]!) || cmpPath(x[1][1]!, y[1][1]!));
      const used = new Set<Change>();
      for (const [sc, r, n] of scores) {
        if (used.has(r) || used.has(n)) continue;
        used.add(r); used.add(n);
        n[0] = 'R' + String(sc).padStart(3, '0'); n[1] = r[1];
        out.splice(out.indexOf(r), 1);
      }
    }
  }
  return out;
}

const RENAME_PAIRS = 10_000; // removed x added paths compared for inexact renames

/** Content split into pieces that end at a newline or after 64 bytes, counted (keys are byte strings), for rename similarity. */
function chunkCounts(data: Uint8Array): [Map<string, number>, number] {
  const counts = new Map<string, number>();
  let i = 0;
  while (i < data.length) {
    const nl = data.subarray(i, i + 64).indexOf(10);
    const end = nl >= 0 ? i + nl + 1 : Math.min(i + 64, data.length);
    const piece = String.fromCharCode(...data.subarray(i, end));
    counts.set(piece, (counts.get(piece) ?? 0) + 1);
    i = end;
  }
  return [counts, data.length];
}

function similarity([cx, nx]: [Map<string, number>, number], [cy, ny]: [Map<string, number>, number]): number {
  if (Math.max(nx, ny) === 0 || Math.min(nx, ny) * 2 < Math.max(nx, ny)) return 0;
  let copied = 0;
  for (const [k, n] of cx) copied += Math.min(n, cy.get(k) ?? 0) * k.length;
  return Math.floor(copied * 100 / Math.max(nx, ny));
}

const score = (st: string) => st.length > 1 ? Number(st.slice(1)) : 100;
const treeLoader = (repo: Repo, a: Files, b: Files) => (side: string, p: string) => repo.typed((side === 'a' ? a : b).get(p)![1], 'blob');

function worktreeFiles(repo: Repo, paths: Iterable<string>): Files {
  const out: Files = new Map();
  for (const p of paths) { const w = repo.wtId(p); if (w !== null) out.set(p, w); }
  return out;
}

const indexFiles = (entries: Iterable<Entry>): Files => {
  const out: Files = new Map();
  for (const e of entries) if (e.stage === 0) out.set(e.path, [e.mode, e.sha]);
  return out;
};

class Status {
  head: string | null;
  entries: Entry[];
  unmerged: Map<string, Map<number, Entry>>;
  staged: Change[];
  unstaged: Change[] = [];
  untracked: string[];
  constructor(repo: Repo, untrackedMode = 'normal') {
    this.head = repo.head();
    this.entries = repo.index();
    const idx = indexFiles(this.entries);
    this.unmerged = repo.unmerged(this.entries);
    const headFiles = treeFiles(repo, this.head);
    this.staged = diffFiles(headFiles, idx, null, treeLoader(repo, headFiles, idx)).filter(c => !this.unmerged.has((c[2] ?? c[1])!));
    const work = worktreeFiles(repo, idx.keys());
    for (const p of sortPaths(idx.keys())) {
      if (this.unmerged.has(p)) continue;
      const w = work.get(p);
      if (!w) this.unstaged.push(['D', p, null]);
      else if (!same(w, idx.get(p))) this.unstaged.push([kindOf(w[0]) !== kindOf(idx.get(p)![0]) ? 'T' : 'M', p, p]);
    }
    const tracked = union(idx.keys(), this.unmerged.keys());
    this.untracked = untrackedMode !== 'no' ? untracked(repo, tracked, untrackedMode) : [];
  }
}

function untracked(repo: Repo, tracked: Set<string>, mode: string): string[] {
  const dirs = new Set<string>();
  for (const p of tracked) {
    const parts = p.split('/');
    for (let k = 1; k < parts.length; k++) dirs.add(parts.slice(0, k).join('/'));
  }
  const out: string[] = [];
  let count = 0;
  const hasFiles = (rel: string) => {
    for (const p of repo.walk(rel)) if (!repo.ignored(rstripChars(p, '/'), p.endsWith('/'))) return true;
    return false;
  };
  const visit = (rel: string) => {
    let names: string[];
    try { names = sortPaths(repo.fs.list(repo.wt(rel))); } catch (e) { if (e instanceof ShellError) return; throw e; }
    for (const name of names) {
      if (++count % 256 === 0) repo.ctx.checkTime();
      if (name === '.git') continue;
      const r = rel ? rel + '/' + name : name;
      const full = repo.wt(r);
      if (repo.fs.isDirectory(full, false)) {
        if (repo.ignored(r, true) || tracked.has(r)) continue;
        if (repo.fs.exists(full + '/.git', false) && !dirs.has(r)) out.push(r + '/');
        else if (dirs.has(r) || mode === 'all') visit(r);
        else if (hasFiles(r)) out.push(r + '/');
      } else if (!tracked.has(r) && !repo.ignored(r, false)) out.push(r);
    }
  };
  visit('');
  return sortPaths(out);
}

function blobData(repo: Repo, side: F | null | undefined, path: string, fromWorktree: boolean): Uint8Array {
  if (!side) return new Uint8Array(0);
  if (fromWorktree) { const s = repo.wtStat(path); return s ? s[1] : new Uint8Array(0); }
  return repo.typed(side[1], 'blob');
}

const abbrev = (sha: string) => sha.slice(0, 7);
const eqBytes = (x: Uint8Array, y: Uint8Array) => x.length === y.length && x.every((v, i) => v === y[i]);

/** 'a => b', or with the shared directories factored out: 'dir/{a => b}/f'. */
function renameLabel(a: string, b: string): string {
  let pre = 0;
  for (let i = 0; i < Math.min(a.length, b.length); i++) {
    if (a[i] !== b[i]) break;
    if (a[i] === '/') pre = i + 1;
  }
  let suf = 0;
  for (let i = 1; i <= Math.min(a.length, b.length) - pre; i++) {
    if (a[a.length - i] !== b[b.length - i]) break;
    if (a[a.length - i] === '/') suf = i;
  }
  if (pre || suf) return `${a.slice(0, pre)}{${a.slice(pre, a.length - suf)} => ${b.slice(pre, b.length - suf)}}${a.slice(a.length - suf)}`;
  return `${a} => ${b}`;
}

type Stat = [st: string, old: string | null, neu: string | null, n: [number, number] | null, sa: number, sb: number];
type Write = (s: string) => void;

/** Prints the differences between two maps in the format opts asks for; true when any. */
function writeDiff(g: Git, repo: Repo, a: Files, b: Files, opts: Opts, specs: string[] | null = null, bWorktree = false, aWorktree = false,
  out?: Write): boolean {
  const write = out ?? ((s: string) => g.out(s));
  const changes = diffFiles(a, b, specs, (side, p) => blobData(repo, (side === 'a' ? a : b).get(p), p, side === 'a' ? aWorktree : bWorktree));
  if (!changes.length) return false;
  if (opts.has('\x05')) return true; // --quiet
  if (opts.has('\x03')) { // --name-only
    for (const [, old, neu] of changes) write(cQuote((neu ?? old)!) + '\n');
    return true;
  }
  if (opts.has('\x04')) { // --name-status
    for (const [st, old, neu] of changes) write(st[0] === 'R' ? `R${String(score(st)).padStart(3, '0')}\t${cQuote(old!)}\t${cQuote(neu!)}\n` : `${st}\t${cQuote((neu ?? old)!)}\n`);
    return true;
  }
  const stats: Stat[] = [];
  for (const [st, old, neu] of changes) {
    const x = blobData(repo, old ? a.get(old) : null, old ?? '', aWorktree);
    const y = blobData(repo, neu ? b.get(neu) : null, neu ?? '', bWorktree);
    if (isBinary(x) || isBinary(y)) stats.push([st, old, neu, null, x.length, y.length]);
    else stats.push([st, old, neu, eqBytes(x, y) ? [0, 0] : countChanges(x, y, g.check), x.length, y.length]);
  }
  if (opts.has('\x06')) { // --numstat
    for (const [st, old, neu, n] of stats) {
      const name = st[0] === 'R' ? `${old} => ${neu}` : (neu ?? old)!;
      write((n === null ? '-\t-\t' : `${n[0]}\t${n[1]}\t`) + name + '\n');
    }
    return true;
  }
  if (opts.has('\x01') || opts.has('\x02') || opts.has('\x1f')) { // --stat / --shortstat / --summary
    if (opts.has('\x01') || opts.has('\x02')) writeStat(write, stats, !opts.has('\x01'));
    if (opts.has('\x1f')) for (const l of summaryLines(changes, a, b)) write(l + '\n');
    return true;
  }
  const u = opts.val('U', '3');
  const context = isDigits(u) ? Number(u) : 3;
  for (const [st, old, neu] of changes) {
    if (st === 'T') { // a file became a link or the other way: shown as a deletion and an addition
      writePatch(g, repo, write, 'D', old, null, a.get(old!), null, context, aWorktree, bWorktree);
      writePatch(g, repo, write, 'A', null, neu, null, b.get(neu!), context, aWorktree, bWorktree);
      continue;
    }
    writePatch(g, repo, write, st, old, neu, old ? a.get(old) : null, neu ? b.get(neu) : null, context, aWorktree, bWorktree);
  }
  return true;
}

function writeStat(write: Write, stats: Stat[], short = false): void {
  const files = stats.length;
  let ins = 0, dels = 0;
  for (const s of stats) if (s[3]) { ins += s[3][0]; dels += s[3][1]; }
  if (!short) {
    const names = stats.map(([st, old, neu]) => st[0] === 'R' ? renameLabel(old!, neu!) : cQuote((neu ?? old)!));
    const counts = stats.map(s => s[3] ? String(s[3][0] + s[3][1]) : 'Bin');
    const cw = Math.max(...counts.map(c => c.length));
    const withN = stats.filter(s => s[3]);
    const maxChange = withN.length ? Math.max(...withN.map(s => s[3]![0] + s[3]![1])) : 0;
    let nw = Math.max(...names.map(cpLen));
    nw = Math.min(nw, 80 - cw - 6);
    const gw = 80 - nw - cw - 6;
    stats.forEach(([, , , n, sa, sb], k) => {
      let name = names[k];
      const count = counts[k];
      if (cpLen(name) > nw) name = '...' + cpSlice(name, cpLen(name) - nw + 3);
      if (n === null) { write(` ${ljust(name, nw)} | ${rjust('Bin', cw)} ${sa} -> ${sb} bytes\n`); return; }
      let [plus, minus] = n;
      if (maxChange > gw && gw > 0) {
        plus = plus ? 1 + Math.floor(plus * (gw - 1) / maxChange) : 0;
        minus = minus ? 1 + Math.floor(minus * (gw - 1) / maxChange) : 0;
      }
      const graph = '+'.repeat(plus) + '-'.repeat(minus);
      write(` ${ljust(name, nw)} | ${rjust(count, cw)}` + (graph ? ` ${graph}` : '') + '\n');
    });
  }
  let line = ` ${files} file${files !== 1 ? 's' : ''} changed`;
  if (ins || !dels) line += `, ${ins} insertion${ins !== 1 ? 's' : ''}(+)`;
  if (dels || !ins) line += `, ${dels} deletion${dels !== 1 ? 's' : ''}(-)`;
  write(line + '\n');
}

function writePatch(g: Git, repo: Repo, write: Write, st: string, old: string | null, neu: string | null, x: F | null | undefined,
  y: F | null | undefined, context: number, aWorktree: boolean, bWorktree: boolean): void {
  const an = (old ?? neu)!, bn = (neu ?? old)!;
  write(`diff --git ${cQuote('a/' + an)} ${cQuote('b/' + bn)}\n`);
  const xd = blobData(repo, x, old ?? '', aWorktree);
  const yd = blobData(repo, y, neu ?? '', bWorktree);
  const xs = x ? x[1] : '0'.repeat(40), ys = y ? y[1] : '0'.repeat(40);
  if (st === 'A') write(`new file mode ${y![0]}\n`);
  else if (st === 'D') write(`deleted file mode ${x![0]}\n`);
  else if (x![0] !== y![0]) write(`old mode ${x![0]}\nnew mode ${y![0]}\n`);
  if (st[0] === 'R') write(`similarity index ${score(st)}%\nrename from ${cQuote(old!)}\nrename to ${cQuote(neu!)}\n`);
  if (xs === ys) return;
  const sameMode = x && y && x[0] === y[0];
  write(`index ${abbrev(xs)}..${abbrev(ys)}` + (sameMode ? ` ${x[0]}` : '') + '\n');
  const left = st === 'A' ? '/dev/null' : cQuote('a/' + an), right = st === 'D' ? '/dev/null' : cQuote('b/' + bn);
  if (isBinary(xd) || isBinary(yd)) { write(msg('binaryDiffer', { a: left, b: right }) + '\n'); return; }
  write(`--- ${left}\n+++ ${right}\n`);
  for (const l of unified(splitLines(xd), splitLines(yd), context, undefined, g.check)) write(l + '\n');
}

function summaryLines(changes: Change[], a: Files, b: Files): string[] {
  const out: string[] = [];
  for (const [st, old, neu] of changes) {
    if (st === 'A') out.push(` create mode ${b.get(neu!)![0]} ${cQuote(neu!)}`);
    else if (st === 'D') out.push(` delete mode ${a.get(old!)![0]} ${cQuote(old!)}`);
    else if (st[0] === 'R') out.push(` rename ${renameLabel(old!, neu!)} (${score(st)}%)`);
    else if (a.get(old!)![0] !== b.get(neu!)![0] && st === 'M') out.push(` mode change ${a.get(old!)![0]} => ${b.get(neu!)![0]} ${cQuote(neu!)}`);
  }
  return out;
}

function statEntries(g: Git, repo: Repo, a: Files, b: Files, changes: Change[]): Stat[] {
  return changes.map(([st, old, neu]): Stat => {
    const x = old ? repo.typed(a.get(old)![1], 'blob') : new Uint8Array(0);
    const y = neu ? repo.typed(b.get(neu)![1], 'blob') : new Uint8Array(0);
    if (isBinary(x) || isBinary(y)) return [st, old, neu, null, x.length, y.length];
    return [st, old, neu, eqBytes(x, y) ? [0, 0] : countChanges(x, y, g.check), x.length, y.length];
  });
}

const otherEntries = (unmerged: Map<string, Map<number, Entry>>) => [...unmerged.values()].flatMap(st => [...st.values()]);

// ---------- init ----------

function gitInit(g: Git, args: string[]): number {
  const o = parse('init', args);
  const ops = [...o.args, ...o.paths];
  if (ops.length > 1) throw usageError('init', msg('tooManyArguments'));
  const target = ops.length ? normalize(g.cwd, ops[0]) : g.cwd;
  const fs = g.ctx.fs;
  if (!fs.exists(target)) fs.mkdir(target, true);
  const gd = target.replace(/\/+$/, '') + '/.git';
  if (fs.isSymlink(gd)) throw fatal('symlinkInGitDir', { path: gd });
  const shown = gd + '/';
  if (fs.isDirectory(gd, false) && fs.isFile(gd + '/HEAD', false)) {
    if (!o.has('q')) g.out(subMsg('init', 'reinitialized', { path: shown }) + '\n');
    return 0;
  }
  let branch = o.val('b') || g.config('init.defaultbranch');
  for (const d of ['objects/info', 'objects/pack', 'refs/heads', 'refs/tags', 'info', 'hooks', 'branches']) fs.mkdir(gd + '/' + d, true);
  if (!branch) branch = 'master';
  if (!/^[A-Za-z0-9._/-]+$/.test(branch) || branch.includes('..') || ['.', '/', '.lock'].some(x => branch!.endsWith(x)) || /^[-/]/.test(branch))
    throw subFatal('init', 'badBranchName', { name: branch });
  fs.writeText(gd + '/HEAD', `ref: refs/heads/${branch}\n`);
  fs.writeText(gd + '/config', '[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = false\n\tlogallrefupdates = true\n');
  fs.writeText(gd + '/description', "Unnamed repository; edit this file 'description' to name the repository.\n");
  fs.writeText(gd + '/info/exclude', "# git ls-files --others --exclude-from=.git/info/exclude\n# Lines that start with '#' are comments.\n");
  if (!o.has('q')) g.out(subMsg('init', 'initialized', { path: shown }) + '\n');
  return 0;
}

// ---------- status ----------

function gitStatus(g: Git, args: string[]): number {
  args = args.map(a => a === '--porcelain=v1' || a === '--porcelain=1' ? '--porcelain' : a);
  let umode = 'normal';
  const rest: string[] = [];
  for (const a of args) {
    const m = /^(?:-u|--untracked-files)(?:=?(no|normal|all))?$/.exec(a);
    if (m) umode = m[1] ?? 'all';
    else rest.push(a);
  }
  const o = parse('status', rest);
  const repo = g.repo();
  const specs = userSpecs(repo, [...o.args, ...o.paths]);
  const s = new Status(repo, umode);
  if (specs !== null) {
    s.staged = s.staged.filter(c => pathspecMatch(specs, (c[2] ?? c[1])!));
    s.unstaged = s.unstaged.filter(c => pathspecMatch(specs, c[1]!));
    s.untracked = s.untracked.filter(u => pathspecMatch(specs, rstripChars(u, '/')) || specs.some(x => x.startsWith(u)));
    s.unmerged = new Map([...s.unmerged].filter(([k]) => pathspecMatch(specs, k)));
  }
  const porcelain = o.has('\x01');
  if (o.has('s') || porcelain) {
    if (o.has('b')) g.out('## ' + branchLine(repo) + '\n');
    const show = porcelain ? (p: string) => shortQuote(p) : (p: string) => shortQuote(repo.show(p));
    for (const line of shortLines(s, show)) g.out(line + '\n');
    return 0;
  }
  g.out(longStatus(repo, s));
  return 0;
}

function shortQuote(p: string): string {
  const q = cQuote(p);
  return q === p && p.includes(' ') ? `"${p}"` : q;
}

function branchLine(repo: Repo): string {
  const b = repo.branch();
  if (b === null) return 'HEAD (no branch)';
  if (!repo.head()) return subMsg('status', 'noCommitsOnBranch', { branch: b });
  const t = trackingBrief(repo, b, () => repo.ctx.checkTime());
  return t === null ? b : `${b}...${t[0]}` + (t[1] ? ` [${t[1]}]` : '');
}

const UNMERGED_CODES: Record<string, string> = { '1,2,3': 'UU', '2,3': 'AA', '1': 'DD', '1,2': 'UD', '1,3': 'DU', '2': 'AU', '3': 'UA' };
const UNMERGED_LABELS: Record<string, string> = {
  UU: 'bothModified', AA: 'bothAdded', DD: 'bothDeleted', UD: 'deletedByThem', DU: 'deletedByUs', AU: 'addedByUs', UA: 'addedByThem',
};
const unmergedCode = (stages: Map<number, Entry>) => UNMERGED_CODES[[...stages.keys()].sort().join(',')];

function shortLines(s: Status, show: (p: string) => string): string[] {
  const rows = new Map<string, [string, string, string | null]>();
  for (const [st, old, neu] of s.staged) rows.set((neu ?? old)!, [st[0], ' ', st[0] === 'R' ? old : null]);
  for (const [st, old] of s.unstaged) {
    if (!rows.has(old!)) rows.set(old!, [' ', ' ', null]);
    rows.get(old!)![1] = st;
  }
  for (const [p, stages] of s.unmerged) rows.set(p, [unmergedCode(stages), '', null]);
  const out: string[] = [];
  for (const p of sortPaths(rows.keys())) {
    const [x, y, old] = rows.get(p)!;
    out.push(`${x}${y} ` + (old ? `${show(old)} -> ${show(p)}` : show(p)));
  }
  for (const u of s.untracked) out.push(`?? ${show(u)}`);
  return out;
}

function longStatus(repo: Repo, s: Status, committing = false): string {
  const M = (key: string, v: Values = {}) => subMsg('status', key, v);
  const out: string[] = [];
  const b = repo.branch();
  if (b === null) {
    const [at, name] = detachedName(repo);
    out.push(M(at ? 'detachedAt' : 'detachedFrom', { commit: name }));
  } else {
    out.push(M('onBranch', { branch: b }));
    const t = s.head ? trackingText(repo, b, () => repo.ctx.checkTime()) : null;
    if (t) out.push(t, '');
  }
  const merging = repo.gexists('MERGE_HEAD');
  if (s.head === null) out.push('', committing ? M('initialCommit') : M('noCommitsYet'), '');
  if (merging) out.push(s.unmerged.size ? M('unmergedHeader') : M('mergeFixed'), '');
  const label = (key: string) => ljust(M(key) + ' ', 12);
  const show = (p: string) => cQuote(repo.show(p));
  if (s.staged.length) {
    out.push(M('toBeCommitted'));
    if (!merging) out.push(s.head === null ? M('hintUnstageNoCommits') : M('hintUnstage'));
    for (const [st, old, neu] of s.staged) {
      const key = ({ A: 'labelNew', M: 'labelModified', D: 'labelDeleted', R: 'labelRenamed', T: 'labelTypechange' } as Values)[st[0]];
      out.push('\t' + label(key) + (st[0] === 'R' ? `${show(old!)} -> ${show(neu!)}` : show((neu ?? old)!)));
    }
    out.push('');
  }
  if (s.unmerged.size) {
    out.push(M('unmergedPaths'));
    if (!merging) out.push(M('hintUnstage'));
    out.push(M('hintResolve'));
    for (const p of sortCp(s.unmerged.keys())) {
      const code = unmergedCode(s.unmerged.get(p)!);
      out.push('\t' + ljust(M(UNMERGED_LABELS[code]) + ' ', 17) + show(p));
    }
    out.push('');
  }
  if (s.unstaged.length) {
    out.push(M('notStaged'));
    out.push(s.unstaged.some(c => c[0] === 'D') ? M('hintAddRm') : M('hintAdd'));
    out.push(M('hintRestore'));
    for (const [st, old] of s.unstaged) {
      const key = ({ M: 'labelModified', D: 'labelDeleted', T: 'labelTypechange' } as Values)[st];
      out.push('\t' + label(key) + show(old!));
    }
    out.push('');
  }
  if (s.untracked.length) {
    out.push(M('untrackedHeader'), M('hintTrack'));
    for (const u of s.untracked) out.push('\t' + show(u));
    out.push('');
  }
  if (!s.staged.length) {
    if (s.unstaged.length || s.unmerged.size) out.push(M('noChangesAdded'));
    else if (s.untracked.length) out.push(M('nothingAddedUntracked'));
    else if (s.head === null) out.push(M('nothingToCommitInitial'));
    else out.push(M('nothingToCommitClean'));
  }
  return out.join('\n') + '\n';
}

// ---------- add / rm / mv ----------

function gitAdd(g: Git, args: string[]): number {
  const o = parse('add', args);
  const repo = g.repo();
  const items = [...o.args, ...o.paths];
  if (!items.length && !o.has('A') && !o.has('u')) { g.err(subMsg('add', 'nothingSpecified') + '\n'); return 0; }
  const specs = items.length ? userSpecs(repo, items)! : [''];
  const entries = repo.index();
  const idx = repo.stage0(entries);
  const unmerged = repo.unmerged(entries);
  const tracked = union(idx.keys(), unmerged.keys());
  // every spec must match something: a tracked path or a work tree file
  const ignoredHits: string[] = [];
  const raws = items.length ? items : ['.'];
  specs.forEach((spec, k) => {
    const raw = raws[k];
    let hit = false;
    const full = repo.wt(spec);
    const trackedHit = [...tracked].some(p => pathspecMatch([spec], p));
    if (trackedHit) hit = true;
    if (spec === '' || repo.fs.exists(full, false)) hit = true;
    if (hasGlob(spec)) for (const p of repo.walk('')) if (pathspecMatch([spec], rstripChars(p, '/'))) { hit = true; break; }
    if (!hit) throw fatal('pathspecNoMatch', { path: raw });
    const isDir = repo.fs.isDirectory(full, false);
    if (spec && repo.fs.exists(full, false) && !trackedHit && repo.ignored(spec, isDir) && !o.has('f')) ignoredHits.push(spec);
  });
  if (ignoredHits.length) {
    g.err(subMsg('add', 'ignoredPaths', { paths: ignoredHits.map(p => repo.show(p)).join('\n') }) + '\n');
    return 1;
  }
  const changed: [string, string][] = [];
  // tracked paths: update or remove
  for (const p of sortCp(tracked)) {
    if (!pathspecMatch(specs, p)) continue;
    const w = repo.wtStat(p);
    if (w === null) { idx.delete(p); unmerged.delete(p); changed.push(['remove', p]); continue; }
    const sha = objectId('blob', w[1]);
    if (unmerged.has(p) || idx.get(p)!.sha !== sha || idx.get(p)!.mode !== w[0]) {
      changed.push(['add', p]);
      if (!o.has('n')) repo.put('blob', w[1]);
      unmerged.delete(p);
      idx.set(p, repo.entryFor(p, w[0], sha));
    }
  }
  if (!o.has('u')) {
    for (const spec of specs) {
      const full = repo.wt(spec);
      let files: string[];
      if (spec && !repo.fs.isDirectory(full, false) && !hasGlob(spec)) {
        files = repo.fs.exists(full, false) && repo.wtReachable(spec) ? [spec] : [];
      } else {
        const base = spec && repo.fs.isDirectory(full, false) ? spec : '';
        files = [...repo.walk(base)].filter(p => !p.endsWith('/') && pathspecMatch([spec], p) && !repo.ignored(p));
        for (const p of repo.walk(base)) if (p.endsWith('/') && pathspecMatch([spec], rstripChars(p, '/'))) throw subFatal('add', 'nestedRepo', { path: rstripChars(p, '/') });
      }
      for (const p of files) {
        if (idx.has(p) || unmerged.has(p)) continue;
        checkPath(p);
        const w = repo.wtStat(p);
        if (w === null) continue;
        const sha = !o.has('n') ? repo.put('blob', w[1]) : objectId('blob', w[1]);
        idx.set(p, repo.entryFor(p, w[0], sha));
        changed.push(['add', p]);
      }
    }
  }
  if (o.has('v') || o.has('n')) {
    const seen = new Set<string>();
    const uniq = changed.filter(c => { const k = c.join('\0'); if (seen.has(k)) return false; seen.add(k); return true; });
    for (const [what, p] of uniq.sort((x, y) => cmpCp(x[1], y[1]))) g.out(`${what} '${repo.show(p)}'\n`);
  }
  if (!o.has('n')) repo.saveIndex([...idx.values(), ...otherEntries(unmerged)]);
  return 0;
}

function gitRm(g: Git, args: string[]): number {
  const o = parse('rm', args);
  const repo = g.repo();
  const items = [...o.args, ...o.paths];
  if (!items.length) throw usageError('rm', subMsg('rm', 'noPathspec'));
  const entries = repo.index();
  const idx = repo.stage0(entries);
  const unmerged = repo.unmerged(entries);
  const tracked = sortCp(union(idx.keys(), unmerged.keys()));
  const head = treeFiles(repo, repo.head());
  const targets: string[] = [];
  for (const raw of items) {
    const spec = repo.fromUser(raw);
    const hits = tracked.filter(p => pathspecMatch([spec], p));
    if (!hits.length) throw fatal('pathspecNoMatch', { path: raw });
    if (!o.has('r') && hits.some(p => p !== spec) && !hasGlob(spec)) throw subFatal('rm', 'notRecursive', { path: raw });
    for (const p of hits) if (!targets.includes(p)) targets.push(p);
  }
  if (!o.has('f')) {
    const staged: string[] = [], local: string[] = [], both: string[] = [];
    for (const p of targets) {
      const e = idx.get(p);
      if (!e) continue;
      const w = repo.wtId(p);
      const inHead = head.get(p);
      const stagedChange = !same(inHead, [e.mode, e.sha]);
      const localChange = w !== null && !same(w, [e.mode, e.sha]);
      if (o.has('\x01')) { // --cached: only refuses when the file matches neither HEAD nor the work tree
        if (stagedChange && localChange && inHead) both.push(p);
        continue;
      }
      if (stagedChange && localChange) both.push(p);
      else if (stagedChange) staged.push(p);
      else if (localChange) local.push(p);
    }
    for (const [keys, paths] of [[['stagedAndLocal', 'stagedAndLocalMany'], both], [['staged', 'stagedMany'], staged], [['local', 'localMany'], local]] as [string[], string[]][]) {
      if (paths.length) throw new GitError(subMsg('rm', keys[paths.length > 1 ? 1 : 0], { paths: paths.map(p => '    ' + repo.show(p)).join('\n') }), 1);
    }
  }
  for (const p of targets) {
    if (!o.has('q')) g.out(`rm '${p}'\n`);
    if (o.has('n')) continue;
    idx.delete(p);
    unmerged.delete(p);
    if (!o.has('\x01')) repo.wtRemove(p);
  }
  if (!o.has('n')) repo.saveIndex([...idx.values(), ...otherEntries(unmerged)]);
  return 0;
}

function gitMv(g: Git, args: string[]): number {
  const o = parse('mv', args);
  const repo = g.repo();
  const items = [...o.args, ...o.paths];
  if (items.length < 2) throw usageError('mv', subMsg('mv', 'usage'));
  const entries = repo.index();
  const idx = repo.stage0(entries);
  const destRaw = items[items.length - 1];
  const dest = repo.fromUser(destRaw);
  const destIsDir = repo.fs.isDirectory(repo.wt(dest), false);
  if (items.length > 2 && !destIsDir) throw subFatal('mv', 'destinationNotDir', { path: destRaw });
  const moves: [string, string, string[]][] = [];
  for (const raw of items.slice(0, -1)) {
    const src = repo.fromUser(raw);
    const target = destIsDir ? (dest ? dest + '/' : '') + src.slice(src.lastIndexOf('/') + 1) : dest;
    const full = repo.wt(src);
    if (!repo.fs.exists(full, false)) throw subFatal('mv', 'badSource', { source: raw, destination: destRaw });
    const under = [...idx.keys()].filter(p => p.startsWith(src + '/'));
    if (!idx.has(src) && !under.length) throw subFatal('mv', 'notUnderControl', { source: raw, destination: destRaw });
    if (repo.fs.exists(repo.wt(target), false) && !o.has('f')) throw subFatal('mv', 'destinationExists', { source: raw, destination: destRaw });
    moves.push([src, target, under]);
  }
  for (const [src, target, under] of moves) {
    if (o.has('v') || o.has('n')) g.out(`Renaming ${repo.show(src)} to ${repo.show(target)}\n`);
    if (o.has('n')) continue;
    if (repo.fs.exists(repo.wt(target), false)) repo.fs.delete(repo.wt(target), true);
    const parent = target.includes('/') ? target.slice(0, target.lastIndexOf('/')) : '';
    if (parent && !repo.fs.isDirectory(repo.wt(parent), false)) throw subFatal('mv', 'destinationNotDir', { path: parent });
    repo.fs.move(repo.wt(src), repo.wt(target));
    for (const p of [...(idx.has(src) ? [src] : []), ...under]) {
      const e = idx.get(p)!;
      idx.delete(p);
      const np = target + p.slice(src.length);
      checkPath(np);
      e.path = np;
      idx.set(np, e);
    }
  }
  if (!o.has('n')) repo.saveIndex([...idx.values(), ...entries.filter(e => e.stage)]);
  return 0;
}

// ---------- commit ----------

function cleanMessage(text: string, stripComments = false): string {
  const out: string[] = [];
  for (let line of text.split('\n')) {
    line = line.trimEnd();
    if (stripComments && line.startsWith('#')) continue;
    if (line === '' && (!out.length || out[out.length - 1] === '')) continue;
    out.push(line);
  }
  while (out.length && out[out.length - 1] === '') out.pop();
  return out.length ? out.join('\n') + '\n' : '';
}

function readMessageFile(g: Git, path: string): string {
  if (path === '-') return g.ctx.stdinText;
  const full = normalize(g.cwd, path);
  if (!g.ctx.fs.isFile(full)) throw new GitError(subMsg('commit', 'cannotReadMessage', { file: path }), 128);
  return bytesToText(g.ctx.fs.readBytes(full));
}

function gitCommit(g: Git, args: string[]): number {
  const o = parse('commit', args);
  const repo = g.repo();
  const entries = repo.index();
  const um = repo.unmerged(entries);
  if (um.size) {
    g.err(subMsg('commit', 'unmergedFiles') + '\n');
    g.out(sortCp(um.keys()).map(p => `U\t${repo.show(p)}\n`).join(''));
    return 128;
  }
  const head = repo.head();
  const amend = o.has('\x01');
  if (amend && head === null) throw subFatal('commit', 'nothingToAmend');
  if (amend && repo.gexists('MERGE_HEAD')) throw subFatal('commit', 'amendDuringMerge');
  const idx = new Map(entries.map(e => [e.path, e]));
  const paths = [...o.args, ...o.paths];
  if (paths.length && o.has('a')) throw subFatal('commit', 'pathsWithAll');
  if (o.has('a')) {
    for (const [p, e] of [...idx]) {
      const w = repo.wtStat(p);
      if (w === null) idx.delete(p);
      else if (objectId('blob', w[1]) !== e.sha || w[0] !== e.mode) idx.set(p, repo.entryFor(p, w[0], repo.put('blob', w[1])));
    }
  }
  let files: Files = new Map([...idx].map(([p, e]) => [p, [e.mode, e.sha] as F]));
  if (paths.length) {
    const specs = userSpecs(repo, paths)!;
    const base = treeFiles(repo, head);
    const tracked = union(idx.keys(), base.keys());
    paths.forEach((raw, k) => {
      if (![...tracked].some(p => pathspecMatch([specs[k]], p))) throw new GitError(subMsg('commit', 'pathspecUnknown', { path: raw }), 1);
    });
    files = new Map(base);
    for (const p of sortCp(tracked)) {
      if (!pathspecMatch(specs, p)) continue;
      const w = repo.wtStat(p);
      if (w === null) { files.delete(p); idx.delete(p); }
      else {
        const sha = repo.put('blob', w[1]);
        files.set(p, [w[0], sha]);
        idx.set(p, repo.entryFor(p, w[0], sha));
      }
    }
  }
  // the message
  const mergeHeads = (repo.gtext('MERGE_HEAD') ?? '').split(/\s+/).filter(x => SHA.test(x));
  const old = amend ? repo.commit(head!) : null;
  let message: string;
  if (o.vals('m').length) message = o.vals('m').join('\n\n');
  else if (o.val('F') !== null) message = readMessageFile(g, o.val('F')!);
  else if (o.has('\x04') && old) message = old.message;
  else if (o.has('\x04') && mergeHeads.length) message = repo.gtext('MERGE_MSG') ?? '';
  else message = g.editorError(old !== null ? old.message : mergeHeads.length ? (repo.gtext('MERGE_MSG') ?? '') : null);
  message = cleanMessage(message, o.val('F') === null && !o.vals('m').length);
  if (!message && !o.has('\x03')) throw new GitError(subMsg('commit', 'emptyMessage'), 1);
  const tree = repo.writeTree(files);
  const parents = old ? [...old.parents] : [...(head ? [head] : []), ...mergeHeads];
  if (!amend && !mergeHeads.length && !o.has('\x02')) {
    const before = head ? repo.commit(head).tree : EMPTY_TREE;
    if (tree === before) {
      repo.saveIndex(idx.values());
      g.out(longStatus(repo, new Status(repo), true));
      return 1;
    }
  }
  // identities
  let aName: string | null = null, aEmail: string | null = null;
  if (o.val('\x05') !== null) {
    const m = /^\s*(.*?)\s*<([^<>]*)>\s*$/.exec(o.val('\x05')!);
    if (!m) throw subFatal('commit', 'badAuthor', { author: o.val('\x05')! });
    [aName, aEmail] = [m[1], m[2]];
  }
  let author: string;
  if (old && aName === null && o.val('\x06') === null) author = old.author;
  else {
    if (old && aName === null) { const [n, e] = parseIdent(old.author); [aName, aEmail] = [n, e]; }
    author = g.ident('author', true, aName, aEmail, o.val('\x06'));
  }
  const committer = g.ident('committer');
  const body = `tree ${tree}\n` + parents.map(p => `parent ${p}\n`).join('') + `author ${author}\ncommitter ${committer}\n\n${message}`;
  const sha = repo.put('commit', body);
  const c = repo.commit(sha);
  const kind = amend ? 'commit (amend)' : mergeHeads.length ? 'commit (merge)' : parents.length ? 'commit' : 'commit (initial)';
  repo.updateRef('HEAD', sha, `${kind}: ${c.subject}`);
  if (!repo.headRef()) repo.gwrite('HEAD', sha + '\n');
  repo.saveIndex(idx.values());
  for (const f of ['MERGE_HEAD', 'MERGE_MSG', 'MERGE_MODE']) repo.gremove(f);
  if (!o.has('q')) g.out(commitSummary(g, repo, c, !parents.length && !amend, amend));
  return 0;
}

function commitSummary(g: Git, repo: Repo, c: Commit, root: boolean, amend = false): string {
  const b = repo.branch();
  const where = b !== null ? b : 'detached HEAD';
  const out = [`[${where}${root ? ' (root-commit)' : ''} ${abbrev(c.sha)}] ${c.subject}`];
  const [an, ae, at, az] = parseIdent(c.author);
  const [cn, ce, ct, cz] = parseIdent(c.committer);
  if (an !== cn || ae !== ce) out.push(` Author: ${an} <${ae}>`);
  if (amend || at !== ct || az !== cz) out.push(` Date: ${formatDate(at, az)}`);
  const a = c.parents.length ? treeFiles(repo, c.parents[0]) : new Map<string, F>();
  const bf = repo.flatten(c.tree);
  const changes = diffFiles(a, bf, null, treeLoader(repo, a, bf));
  let text = '';
  if (changes.length) writeStat(s => { text += s; }, statEntries(g, repo, a, bf, changes), true);
  return out.join('\n') + '\n' + text + summaryLines(changes, a, bf).map(l => l + '\n').join('');
}

// ---------- log / show ----------

function decorations(repo: Repo): Map<string, string[]> {
  const out = new Map<string, string[]>();
  const headRef = repo.headRef();
  const remotes: [string, string][] = remoteBranches(repo).filter(r => r[1]).map(([n, sha]) => ['refs/remotes/' + n, sha!]);
  const refs = [...repo.refs('refs/heads/'), ...repo.refs('refs/tags/'), ...remotes].sort((x, y) => cmpPath(y[0], x[0]));
  for (const [name, sha] of refs) {
    if (name === headRef) continue;
    const target = name.startsWith('refs/tags/') ? peel(repo, sha, 'commit') : sha;
    const label = name.startsWith('refs/tags/') ? 'tag: ' + name.slice(10) : name.startsWith('refs/remotes/') ? name.slice(13) : name.slice(11);
    const k = target || sha;
    if (!out.has(k)) out.set(k, []);
    out.get(k)!.push(label);
  }
  const head = repo.head();
  if (head) {
    const b = repo.branch();
    if (!out.has(head)) out.set(head, []);
    out.get(head)!.unshift(b !== null ? `HEAD -> ${b}` : 'HEAD');
  }
  return out;
}

function expandFormat(g: Git, repo: Repo, fmt: string, c: Commit, decor: Map<string, string[]> | null, dateStyle: string): string {
  const [an, ae, at, az] = parseIdent(c.author);
  const [cn, ce, ct, cz] = parseIdent(c.committer);
  if (decor === null && (fmt.includes('%d') || fmt.includes('%D'))) decor = g.decor = g.decor ?? decorations(repo);
  const d = decor?.get(c.sha) ?? [];
  const date = (epoch: number, zone: string, style: string) => formatDate(epoch, zone, style, g.now());
  const body = c.body;
  const simple: [string, string][] = Object.entries({
    H: c.sha, h: abbrev(c.sha), T: c.tree, t: abbrev(c.tree), P: c.parents.join(' '), p: c.parents.map(abbrev).join(' '),
    an, ae, al: ae.split('@')[0], ad: date(at, az, dateStyle), aD: date(at, az, 'rfc'), ar: date(at, az, 'relative'),
    at: String(at), ai: date(at, az, 'iso'), aI: date(at, az, 'iso-strict'), as: date(at, az, 'short'),
    cn, ce, cl: ce.split('@')[0], cd: date(ct, cz, dateStyle), cD: date(ct, cz, 'rfc'), cr: date(ct, cz, 'relative'),
    ct: String(ct), ci: date(ct, cz, 'iso'), cI: date(ct, cz, 'iso-strict'), cs: date(ct, cz, 'short'),
    s: c.subject, b: body.endsWith('\n') || !body ? body : body + '\n', B: c.message,
    d: d.length ? ` (${d.join(', ')})` : '', D: d.join(', '), n: '\n', '%': '%',
    Cred: '', Cgreen: '', Cblue: '', Creset: '',
  });
  const keys = simple.map((kv, i) => [kv, i] as const).sort((x, y) => y[0][0].length - x[0][0].length || x[1] - y[1]).map(x => x[0]);
  let out = '';
  let i = 0;
  while (i < fmt.length) {
    const ch = fmt[i];
    if (ch !== '%') { out += ch; i++; continue; }
    if (fmt.startsWith('%C(', i)) {
      const j = fmt.indexOf(')', i);
      if (j > 0) { i = j + 1; continue; }
    }
    const m = /^x([0-9a-fA-F]{2})/.exec(fmt.slice(i + 1, i + 4));
    if (m) { out += String.fromCharCode(parseInt(m[1], 16)); i += 4; continue; }
    const hit = keys.find(([k]) => fmt.startsWith(k, i + 1));
    if (hit) { out += hit[1]; i += 1 + hit[0].length; } else { out += '%'; i++; }
  }
  return out;
}

const PRETTY = ['oneline', 'short', 'medium', 'full', 'fuller', 'raw'];

/** The commit as log prints it, ending in a newline (except a separator-style format). parents: --parents. */
function commitHeader(g: Git, repo: Repo, c: Commit, pretty: string, fmt: string | null, decor: Map<string, string[]> | null, dateStyle: string,
  abbrevCommit: boolean, parents = false): string {
  if (pretty === 'format' || pretty === 'tformat') return expandFormat(g, repo, fmt ?? '', c, decor, dateStyle) + (pretty === 'tformat' ? '\n' : '');
  const d = decor?.get(c.sha) ?? [];
  const dtext = d.length ? ` (${d.join(', ')})` : '';
  const sha = [c.sha, ...(parents ? c.parents : [])].map(x => abbrevCommit ? abbrev(x) : x).join(' ');
  if (pretty === 'oneline') return `${sha}${dtext} ${c.subject}\n`;
  const [an, ae, at, az] = parseIdent(c.author);
  const [cn, ce, ct, cz] = parseIdent(c.committer);
  let lines = [`commit ${sha}${dtext}`];
  if (c.parents.length > 1) lines.push('Merge: ' + c.parents.map(abbrev).join(' '));
  if (pretty === 'raw') lines = [`commit ${sha}`, `tree ${c.tree}`, ...c.parents.map(p => `parent ${p}`), `author ${c.author}`, `committer ${c.committer}`];
  else if (pretty === 'fuller') lines.push(`Author:     ${an} <${ae}>`, `AuthorDate: ${formatDate(at, az, dateStyle, g.now())}`,
    `Commit:     ${cn} <${ce}>`, `CommitDate: ${formatDate(ct, cz, dateStyle, g.now())}`);
  else {
    lines.push(`Author: ${an} <${ae}>`);
    if (pretty === 'medium') lines.push(`Date:   ${formatDate(at, az, dateStyle, g.now())}`);
    if (pretty === 'full') lines.push(`Commit: ${cn} <${ce}>`);
  }
  lines.push('');
  const text = rstripChars(c.message, '\n');
  const msgLines = pretty === 'short' ? [c.subject] : text ? text.split('\n') : [];
  lines.push(...msgLines.map(l => l ? '    ' + l : '    '));
  return lines.join('\n') + '\n';
}

function prettyChoice(o: Opts): [string, string | null] {
  if (o.has('\x0e')) return ['oneline', null];
  const v = o.val('\x10');
  if (v === null) return ['medium', null];
  if (PRETTY.includes(v)) return [v, null];
  if (v.startsWith('format:')) return ['format', v.slice(7)];
  if (v.startsWith('tformat:')) return ['tformat', v.slice(8)];
  if (v.includes('%') || v === '') return ['tformat', v];
  throw new GitError(msg('badPretty', { format: v }), 128);
}

function diffWanted(o: Opts, defaultPatch: boolean): boolean {
  if (o.has('s')) return false;
  return defaultPatch || ['p', 'u', '\x01', '\x02', '\x03', '\x04', '\x06', '\x1f'].some(k => o.has(k));
}

/** The commit's changes against its first parent, as text, in the format the options ask for (patch by default). A
 * merge shows nothing, except to show, which lists its stat or names against the first parent (never a patch). */
function commitDiff(g: Git, repo: Repo, c: Commit, o: Opts, specs: string[] | null, merges = false): string {
  if (c.parents.length > 1 && !(merges && ['\x01', '\x02', '\x03', '\x04', '\x06', '\x1f'].some(k => o.has(k)))) return '';
  const a = c.parents.length ? treeFiles(repo, c.parents[0]) : new Map<string, F>();
  const b = repo.flatten(c.tree);
  let buf = '';
  writeDiff(g, repo, a, b, o, specs, false, false, s => { buf += s; });
  return buf;
}

/** Splits operands into revisions (with ^x / a..b exclusions) and paths, the way log and diff tell them apart. */
function revisionsAndPaths(repo: Repo, o: Opts): [string[], string[], string[]] {
  const include: string[] = [], exclude: string[] = [], paths = [...o.paths];
  for (let k = 0; k < o.args.length; k++) {
    const a = o.args[k];
    if (a.startsWith('^') && a.length > 1) { exclude.push(commitOf(repo, a.slice(1))); continue; }
    if (a.includes('...')) {
      const [left, , right] = partition(a, '...');
      const lc = commitOf(repo, left || 'HEAD'), rc = commitOf(repo, right || 'HEAD');
      include.push(lc, rc);
      const rs = ancestors(repo, [rc], repo.g.check);
      exclude.push(...[...ancestors(repo, [lc], repo.g.check)].filter(c => rs.has(c)).sort());
      continue;
    }
    if (a.includes('..') && !a.includes('...')) {
      const [left, , right] = partition(a, '..');
      exclude.push(commitOf(repo, left || 'HEAD'));
      include.push(commitOf(repo, right || 'HEAD'));
      continue;
    }
    const sha = tryResolve(repo, a);
    if (sha !== null) {
      const c = peel(repo, sha, 'commit');
      if (c === null) throw fatal('notCommit', { rev: a });
      include.push(c);
      continue;
    }
    if (repo.fs.exists(normalize(repo.g.cwd, a), false) && !o.dashdash) { paths.push(...o.args.slice(k)); break; }
    throw fatal('badRevision', { rev: a });
  }
  return [include, exclude, paths];
}

const allRefCommits = (repo: Repo) => repo.refs('refs/').map(([, s]) => peel(repo, s, 'commit')).filter((c): c is string => c !== null);
const dedupe = (xs: string[]) => [...new Set(xs)];

function gitLog(g: Git, args: string[]): number {
  args = args.map(a => /^-\d+$/.test(a) ? `-n${a.slice(1)}` : a);
  const o = parse('log', args);
  const repo = g.repo();
  let [include, exclude, paths] = revisionsAndPaths(repo, o);
  if (o.has('\x12')) {
    include = [...include, ...allRefCommits(repo)];
    if (repo.head()) include.push(repo.head()!);
  }
  if (!include.length && !o.has('\x12')) {
    const h = repo.head();
    if (h === null) throw subFatal('log', 'noCommitsYet', { branch: repo.branch() || 'HEAD' });
    include = [h];
  }
  const specs = userSpecs(repo, paths);
  const nv = o.val('n');
  const limit = nv !== null && /^-?\d+$/.test(nv) ? Number(nv) : -1;
  const sv = o.val('\x1b', '0');
  let skip = isDigits(sv) ? Number(sv) : 0;
  const [pretty, fmt] = prettyChoice(o);
  const dateStyle = o.val('\x19', 'default');
  formatDate(0, '+0000', dateStyle); // rejects an unknown style before any output
  const decor = o.has('\x11') ? decorations(repo) : null;
  const abbrevCommit = o.has('\x1a') || o.has('\x0e');
  const excluded = exclude.length ? ancestors(repo, exclude, g.check) : new Set<string>();
  let order: string[];
  if (o.has('\x16')) {
    order = [];
    let c: string | null = include.length ? include[0] : null;
    while (c && !excluded.has(c) && order.length <= MAX_WALK) {
      order.push(c);
      const ps: string[] = repo.commit(c).parents;
      c = ps.length ? ps[0] : null;
    }
  } else order = walkOrder(repo, dedupe(include), g.check).filter(c => !excluded.has(c));
  let since: number | null = null, until: number | null = null;
  for (const [key, name] of [['\x1d', 'since'], ['\x1e', 'until']]) {
    const v = o.val(key);
    if (v === null) continue;
    let when: number;
    try { when = parseDate(v, g.now())[0]; } catch (e) {
      if (e instanceof DateError) throw new GitError(msg('badDate', { date: v }), 128);
      throw e;
    }
    if (name === 'since') since = when; else until = when;
  }
  const authorRx = userRegex(o.val('\x14'));
  const grepRx = userRegex(o.val('\x15'));
  const pickaxe = o.val('S');
  let pickaxeRx: RegExp | null = null;
  const gv = o.val('G');
  if (gv !== null) {
    const error = posixRegexError(gv, true, false);
    if (error) throw subFatal('log', 'badPickaxeRegex', { error });
    pickaxeRx = new RegExp(posixToJs(gv, true), 'm');
  }
  const shown: Commit[] = [];
  const hits = new Map<string, string[]>(); // commit -> the paths the pickaxe found, which its diff is limited to
  for (const sha of order) {
    const c = repo.commit(sha);
    if ((o.has('\x17') && c.parents.length > 1) || (o.has('\x18') && c.parents.length < 2)) continue;
    if ((since !== null && commitTime(repo, sha) < since) || (until !== null && commitTime(repo, sha) > until)) continue;
    if (authorRx) {
      const gt = c.author.lastIndexOf('>');
      if (!authorRx.test((gt < 0 ? c.author : c.author.slice(0, gt)) + '>')) continue;
    }
    if (grepRx && !grepRx.test(c.message)) continue;
    if (specs !== null) {
      const a = c.parents.length ? treeFiles(repo, c.parents[0]) : new Map<string, F>();
      if (!diffFiles(a, repo.flatten(c.tree), specs).length) continue;
    }
    if (pickaxe !== null || pickaxeRx !== null) {
      if (c.parents.length > 1) continue; // a merge has no diff of its own to search
      const found = pickaxePaths(g, repo, c, specs, pickaxe, pickaxeRx);
      if (!found.length) continue;
      hits.set(sha, found);
    }
    if (skip) { skip--; continue; }
    if (limit >= 0 && shown.length >= limit) break;
    shown.push(c);
  }
  if (o.has('\x13')) shown.reverse();
  writeLog(g, repo, shown, o, pretty, fmt, decor, dateStyle, abbrevCommit, specs, false, o.has('\x0f'), false, hits);
  return 0;
}

/** The files whose change in c alters the count of needle (-S), or adds or removes a line rx matches (-G). */
function pickaxePaths(g: Git, repo: Repo, c: Commit, specs: string[] | null, needle: string | null, rx: RegExp | null): string[] {
  const a = c.parents.length ? treeFiles(repo, c.parents[0]) : new Map<string, F>();
  const b = repo.flatten(c.tree);
  const out: string[] = [];
  const add = (...ps: (string | null)[]) => { for (const p of ps) if (p && !out.includes(p)) out.push(p); };
  for (const [, old, neu] of diffFiles(a, b, specs)) {
    g.ctx.checkTime();
    const x = old ? repo.typed(a.get(old)![1], 'blob') : new Uint8Array(0);
    const y = neu ? repo.typed(b.get(neu)![1], 'blob') : new Uint8Array(0);
    if (isBinary(x) || isBinary(y)) continue;
    if (needle !== null) {
      const count = (t: string) => t.split(needle).length - 1;
      if (needle && count(decodeBytes(x)) !== count(decodeBytes(y))) add(old, neu);
      continue;
    }
    const lx = splitLines(x), ly = splitLines(y);
    const changed = myers(lx, ly, g.check).flatMap(([s1, e1, s2, e2]) => [...lx.slice(s1, e1), ...ly.slice(s2, e2)]);
    if (changed.some(l => rx!.test(rstripChars(l, '\n')))) add(old, neu);
  }
  return out;
}

function writeLog(g: Git, repo: Repo, commits: Commit[], o: Opts, pretty: string, fmt: string | null, decor: Map<string, string[]> | null,
  dateStyle: string, abbrevCommit: boolean, specs: string[] | null, defaultPatch: boolean, graph = false, merges = false,
  hits: Map<string, string[]> | null = null): void {
  if (graph) {
    commits.forEach((c, i) => { // a straight line would misdraw it
      if (c.parents.length > 1 || (i + 1 < commits.length && !(c.parents.length === 1 && c.parents[0] === commits[i + 1].sha))) throw fatal('graphNotLinear');
    });
  }
  const wantsDiff = diffWanted(o, defaultPatch);
  commits.forEach((c, i) => {
    g.ctx.checkTime();
    // shortcut: with paths, --parents prints the real parents, not git's rewritten (simplified) ones
    const head = commitHeader(g, repo, c, pretty, fmt, decor, dateStyle, abbrevCommit, o.has('\x05'));
    const diff = wantsDiff ? commitDiff(g, repo, c, o, hits?.size ? hits.get(c.sha)! : specs, merges) : '';
    const multiLine = !['oneline', 'format', 'tformat'].includes(pretty);
    let text = '';
    if (i && multiLine) text += '\n';
    if (pretty === 'format') {
      text += (i ? '\n' : '') + head;
      if (diff) text += '\n' + diff;
    } else {
      text += head;
      if (diff) text += (pretty !== 'oneline' ? '\n' : '') + diff;
    }
    if (graph) {
      const last = i === commits.length - 1;
      const lines = text.split('\n');
      let end = '';
      if (lines.length && lines[lines.length - 1] === '') { lines.pop(); end = '\n'; }
      if (i && multiLine && lines.length && lines[0] === '') lines[0] = '| ';
      const start = i && multiLine ? 1 : 0;
      const out = lines.slice(0, start);
      lines.slice(start).forEach((line, k) => out.push((k === 0 ? '* ' : last ? '  ' : '| ') + line));
      text = out.join('\n') + end;
    }
    g.out(text);
  });
}

function gitShow(g: Git, args: string[]): number {
  const o = parse('show', args);
  const repo = g.repo();
  const targets = o.args.length ? o.args : ['HEAD'];
  const [pretty, fmt] = prettyChoice(o);
  const dateStyle = o.val('\x19', 'default');
  const decor = o.has('\x11') ? decorations(repo) : null;
  let first = true;
  const seen = new Set<string>();
  for (const t of targets) {
    let sha = tryResolve(repo, t);
    if (sha === null) {
      if (t === 'HEAD' && repo.head() === null) throw fatal('badDefaultRevision');
      missingPath(repo, t);
      throw fatal('badRevision', { rev: t });
    }
    if (seen.has(sha)) continue;
    seen.add(sha);
    const [kind, data] = repo.obj(sha);
    if (kind === 'blob') { g.out(decodeBytes(data)); continue; }
    if (kind === 'tree') {
      g.out(`tree ${t}\n\n` + repo.tree(sha).map(([m, n]) => n + (m === '040000' ? '/' : '') + '\n').join(''));
      continue;
    }
    if (kind === 'tag') {
      const [head, , body] = partition(decodeBytes(data), '\n\n');
      const fields = new Map<string, string>();
      for (const l of head.split('\n')) if (l.includes(' ')) { const [k, , v] = partition(l, ' '); fields.set(k, v); }
      const [tn, te, tt, tz] = parseIdent(fields.get('tagger') ?? '');
      g.out(`tag ${fields.get('tag') ?? ''}\nTagger: ${tn} <${te}>\nDate:   ${formatDate(tt, tz, dateStyle, g.now())}\n\n${rstripChars(body, '\n')}\n\n`);
      sha = peel(repo, sha, 'commit');
      if (sha === null) continue;
    }
    const c = repo.commit(sha);
    if (!first && !['oneline', 'format', 'tformat'].includes(pretty)) g.out('\n');
    first = false;
    writeLog(g, repo, [c], o, pretty, fmt, decor, dateStyle, o.has('\x1a') || o.has('\x0e'), userSpecs(repo, o.paths), true, false, true);
  }
  return 0;
}

// ---------- diff ----------

function gitDiff(g: Git, args: string[]): number {
  const o = parse('diff', args);
  const repo = g.repo();
  const cached = o.has('\x08');
  const revs: string[] = [], paths = [...o.paths];
  for (let k = 0; k < o.args.length; k++) {
    const a = o.args[k];
    if (a.includes('...')) {
      const [left, , right] = partition(a, '...');
      const lc = commitOf(repo, left || 'HEAD'), rc = commitOf(repo, right || 'HEAD');
      const base = mergeBase(repo, lc, rc, g.check);
      revs.push(base || lc, rc);
      continue;
    }
    if (a.includes('..')) {
      const [left, , right] = partition(a, '..');
      revs.push(commitOf(repo, left || 'HEAD'), commitOf(repo, right || 'HEAD'));
      continue;
    }
    const sha = tryResolve(repo, a);
    if (sha !== null) { revs.push(sha); continue; }
    if (repo.fs.exists(normalize(g.cwd, a), false) && !o.dashdash) { paths.push(...o.args.slice(k)); break; }
    throw fatal('badRevision', { rev: a });
  }
  const specs = userSpecs(repo, paths);
  const entries = repo.index();
  const idx = indexFiles(entries);
  for (const [p, stages] of repo.unmerged(entries)) if (stages.has(2)) idx.set(p, [stages.get(2)!.mode, stages.get(2)!.sha]);
  const treeOf = (rev: string) => {
    const t = peel(repo, rev, 'tree');
    if (t === null) throw fatal('notTree', { rev });
    return repo.flatten(t);
  };
  if (revs.length > 2) throw usageError('diff', subMsg('diff', 'tooManyRevisions'));
  let a: Files, b: Files, bw: boolean;
  if (revs.length === 2) [a, b, bw] = [treeOf(revs[0]), treeOf(revs[1]), false];
  else if (cached) {
    const base = revs.length ? revs[0] : repo.head();
    [a, b, bw] = [base ? treeOf(base) : new Map(), idx, false];
  } else if (revs.length) {
    a = treeOf(revs[0]);
    [b, bw] = [worktreeFiles(repo, union(idx.keys(), a.keys())), true];
  } else [a, b, bw] = [idx, worktreeFiles(repo, idx.keys()), true];
  if (o.has('\x10') && (o.has('\x03') || o.has('\x04'))) throw subFatal('diff', 'checkWith');
  if (o.has('\x10') && !o.has('\x05')) {
    const changes = diffFiles(a, b, specs);
    const bad = whitespaceCheck(g, repo, a, b, changes, bw);
    return (bad ? 2 : 0) | (changes.length && o.has('\x07') ? 1 : 0);
  }
  const changed = writeDiff(g, repo, a, b, o, specs, bw);
  return changed && (o.has('\x07') || o.has('\x05')) ? 1 : 0;
}

/** diff --check: prints added lines with whitespace problems (git's default core.whitespace); true when any. */
function whitespaceCheck(g: Git, repo: Repo, a: Files, b: Files, changes: Change[], bWorktree: boolean): boolean {
  const blank = (line: string) => !stripChars(line, ' \t\r\n\v\f');
  const trailingBlanks = (lines: string[]) => {
    let n = 0;
    while (n < lines.length && blank(lines[lines.length - 1 - n])) n++;
    return n;
  };
  let bad = false;
  for (const [st, old, neu] of changes) {
    if (!neu || st === 'T') continue;
    const x = blobData(repo, old ? a.get(old) : null, old ?? '', false);
    const y = blobData(repo, b.get(neu), neu, bWorktree);
    if (isBinary(x) || isBinary(y)) continue;
    const lx = splitLines(x), ly = splitLines(y);
    const name = cQuote(neu);
    for (const [, , s2, e2] of myers(lx, ly, g.check)) {
      for (let j = s2; j < e2; j++) {
        const text = rstripChars(ly[j], '\n');
        const problems: string[] = [];
        if (text && ' \t\r'.includes(text[text.length - 1])) problems.push(subMsg('diff', 'checkTrailing'));
        const indent = /^[ \t]*/.exec(text)![0];
        if (indent.length < text.length && indent.includes(' \t')) problems.push(subMsg('diff', 'checkSpaceTab'));
        if (problems.length) {
          bad = true;
          g.out(subMsg('diff', 'checkLine', { file: name, line: String(j + 1), problems: problems.join(', ') }) + '\n');
          g.out('+' + text + '\n');
        }
      }
    }
    const tail = trailingBlanks(ly);
    if (tail > trailingBlanks(lx)) {
      bad = true;
      g.out(subMsg('diff', 'checkBlankEof', { file: name, line: String(ly.length - tail + 1) }) + '\n');
    }
  }
  return bad;
}

// ---------- moving between commits ----------

function validBranchName(name: string): boolean {
  if (!name || name === 'HEAD' || name === '@' || /^[-/.]/.test(name) || ['/', '.', '.lock'].some(x => name.endsWith(x))) return false;
  if (name.includes('..') || name.includes('//') || name.includes('@{') || name.includes('/.')) return false;
  return ![...' ~^:?*[\\\x7f'].some(c => name.includes(c)) && ![...name].some(c => c.charCodeAt(0) < 32);
}

/** Moves the index and work tree from commit old to commit new, keeping local changes to paths the two do not differ
 * in. Refuses (exit 1) when that would lose a local change or an untracked file. Returns the paths that stay changed. */
function switchTrees(g: Git, repo: Repo, old: string | null, neu: string | null, force: boolean, verb = 'checkout'): string[] {
  const a = treeFiles(repo, old), b = treeFiles(repo, neu);
  const entries = repo.index();
  const um = repo.unmerged(entries);
  if (um.size && !force)
    throw new GitError(subMsg('checkout', 'resolveIndexFirst', { paths: sortCp(um.keys()).map(p => msg('needsMerge', { path: p })).join('\n') }), 1);
  const idx = indexFiles(entries);
  if (force) {
    const idxEntries: Entry[] = [];
    for (const [p, [mode, sha]] of b) {
      if (!same(repo.wtId(p), [mode, sha])) repo.wtWrite(p, mode, sha);
      idxEntries.push(repo.entryFor(p, mode, sha));
    }
    for (const p of union(idx.keys(), a.keys(), um.keys())) if (!b.has(p)) repo.wtRemove(p);
    repo.saveIndex(idxEntries);
    return [];
  }
  const plan: string[] = [], dirty: string[] = [], clobber: string[] = [];
  for (const p of sortCp(union(a.keys(), b.keys()))) {
    if (same(a.get(p), b.get(p))) continue;
    g.ctx.checkTime();
    const i = idx.get(p), w = repo.wtId(p);
    if (same(i, b.get(p)) && same(w, i)) continue; // already as the target has it
    if (!a.has(p) && !i) {
      if (w !== null && !repo.ignored(p)) clobber.push(p);
    } else if (!same(i, a.get(p)) || (i && !same(w, i))) dirty.push(p);
    plan.push(p);
  }
  if (dirty.length) {
    throw new GitError(subMsg('checkout', 'localChanges', {
      verb, paths: rstripChars(dirty.map(p => `\t${p}\n`).join(''), '\n'),
      advice: subMsg('checkout', verb === 'merge' ? 'localChangesAdviceMerge' : 'localChangesAdvice'),
    }), 1);
  }
  if (clobber.length) {
    throw new GitError(subMsg('checkout', 'untrackedOverwritten', {
      verb, paths: rstripChars(clobber.map(p => `\t${p}\n`).join(''), '\n'),
      advice: subMsg('checkout', verb === 'merge' ? 'untrackedAdviceMerge' : 'untrackedAdvice'),
    }), 1);
  }
  const byPath = repo.stage0(entries);
  for (const p of plan) {
    const t = b.get(p);
    if (!t) { repo.wtRemove(p); byPath.delete(p); }
    else { repo.wtWrite(p, ...t); byPath.set(p, repo.entryFor(p, ...t)); }
  }
  repo.saveIndex(byPath.values());
  // what stays changed locally, against the new commit
  const out: string[] = [];
  const now = indexFiles(byPath.values());
  for (const p of sortCp(union(now.keys(), b.keys()))) {
    if (!same(now.get(p), b.get(p)) || (now.has(p) && !same(repo.wtId(p), now.get(p))))
      out.push((!now.has(p) || repo.wtId(p) === null ? 'D' : !b.has(p) ? 'A' : 'M') + '\t' + p);
  }
  return out;
}

const subjectOf = (repo: Repo, sha: string) => `${abbrev(sha)} ${repo.commit(sha).subject}`;

function moveHead(g: Git, repo: Repo, targetRef: string | null, targetSha: string | null, label: string, force: boolean, quiet: boolean,
  newBranch: string | null = null, resetBranch = false, stay = false, track: string | null = null): number {
  const old = repo.head();
  const oldBranch = repo.branch();
  const M = (key: string, v: Values = {}) => subMsg('checkout', key, v);
  if (targetRef !== null && targetRef === repo.headRef() && newBranch === null && !stay) {
    if (!quiet) {
      g.err(M('alreadyOn', { branch: label }) + '\n');
      reportTracking(g, repo);
    }
    return 0;
  }
  const carried = switchTrees(g, repo, old, targetSha, force);
  if (force) for (const f of ['MERGE_HEAD', 'MERGE_MSG', 'MERGE_MODE']) repo.gremove(f); // a forced switch gives up a merge in progress
  if (stay) {
    if (!quiet) for (const line of carried) g.out(line + '\n');
    return 0;
  }
  if (newBranch !== null) {
    if (targetSha) repo.updateRef('refs/heads/' + newBranch, targetSha, `branch: Created from ${label}`);
    if (track) setTracking(g, repo, newBranch, track, quiet);
  }
  const fromName = oldBranch !== null ? oldBranch : (old ?? '');
  if (targetRef !== null) repo.setHead(targetRef, `checkout: moving from ${fromName} to ${newBranch || label}`);
  else repo.setHead(targetSha!, `checkout: moving from ${fromName} to ${label}`, true);
  if (quiet) return 0;
  if (oldBranch === null && old && (targetRef !== null || old !== targetSha)) g.err(M('previousPosition', { commit: subjectOf(repo, old) }) + '\n');
  if (targetRef === null) {
    g.err(M('headNowAt', { commit: subjectOf(repo, targetSha!) }) + '\n');
  } else if (newBranch !== null) g.err(M(resetBranch ? 'switchedResetBranch' : 'switchedNewBranch', { branch: newBranch }) + '\n');
  else g.err(M('switchedBranch', { branch: label }) + '\n');
  for (const line of carried) g.out(line + '\n');
  if (targetRef !== null && newBranch === null) reportTracking(g, repo);
  return 0;
}

function reportTracking(g: Git, repo: Repo): void {
  const b = repo.branch();
  const t = b ? trackingText(repo, b, g.check) : null;
  if (t) g.out(t + '\n');
}

/** The remote-tracking branch a start point names (origin/x or refs/remotes/origin/x), for setting up tracking. */
function remoteStart(repo: Repo, start: string | null): string | null {
  if (start === null || tryResolve(repo, start) === null) return null;
  for (const ref of [start, 'refs/' + start, 'refs/remotes/' + start])
    if (ref.startsWith('refs/remotes/') && !ref.endsWith('/HEAD') && repo.readRef(ref) && !repo.readRef('refs/heads/' + start)) return ref;
  return null;
}

/** Records ref (refs/remotes/<remote>/<name>) as branch's upstream, as git does for a branch started from it. */
function setTracking(g: Git, repo: Repo, branch: string, ref: string, quiet: boolean): void {
  const [remote, , name] = partition(ref.slice(13), '/');
  const cfg = repo.config();
  cfg.set(`branch.${branch}.remote`, remote);
  cfg.set(`branch.${branch}.merge`, 'refs/heads/' + name);
  repo.saveConfig();
  if (!quiet) g.out(subMsg('checkout', 'trackingSetUp', { branch, upstream: ref.slice(13) }) + '\n');
}

/** refs/remotes/<remote>/<name> when exactly one remote has a branch called name (git checkout <name> then creates it). */
function guessRemote(repo: Repo, name: string): string | null {
  if (!validBranchName(name)) return null;
  const configured = new Set(repo.config().items().map(([k]) => k).filter(k => k.startsWith('remote.') && k.endsWith('.fetch'))
    .map(k => k.slice(7, k.lastIndexOf('.'))));
  const hits = remoteBranches(repo).filter(([n, , sym]) => sym === null && partition(n, '/')[2] === name && configured.has(partition(n, '/')[0])).map(r => r[0]);
  if (hits.length > 1) throw subFatal('checkout', 'matchedMultiple', { name, count: String(hits.length) });
  return hits.length ? 'refs/remotes/' + hits[0] : null;
}

/** name is a file in the work tree or the index, so 'git checkout name' could mean it. */
function isWorkPath(repo: Repo, name: string): boolean {
  let rel: string;
  try { rel = repo.fromUser(name); } catch (e) { if (e instanceof GitError) return false; throw e; }
  return repo.stage0().has(rel) || repo.fs.exists(repo.wt(rel), false);
}

/** checkout [tree-ish] -- paths / restore: copies paths from a commit (or the index) into the index and/or work tree. */
function checkoutPaths(g: Git, repo: Repo, source: string | null, items: string[], sub: string, staged = false, worktree = true, quiet = false): number {
  const entries = repo.index();
  const idx = repo.stage0(entries);
  const unmerged = repo.unmerged(entries);
  const src = source ? treeFiles(repo, source) : null;
  const specs = items.map(p => repo.fromUser(p));
  const pool = src !== null ? new Set(src.keys()) : union(idx.keys(), unmerged.keys());
  if (staged && src !== null) for (const p of idx.keys()) pool.add(p);
  let count = 0;
  items.forEach((raw, k) => {
    if (![...pool].some(p => pathspecMatch([specs[k]], p))) throw new GitError(msg('pathspecNoMatchKnown', { path: raw }), 1);
  });
  for (const p of sortCp(pool)) {
    if (!pathspecMatch(specs, p)) continue;
    g.ctx.checkTime();
    const t: F | null = src !== null ? (src.get(p) ?? null) : (idx.has(p) ? [idx.get(p)!.mode, idx.get(p)!.sha] : null);
    if (t === null && src === null && unmerged.has(p)) throw new GitError(subMsg(sub, 'unmergedPath', { path: p }), 1);
    if (staged) {
      if (t === null) idx.delete(p);
      else {
        const old = idx.get(p);
        idx.set(p, old && old.mode === t[0] && old.sha === t[1] ? old : new Entry(p, t[1], t[0]));
      }
    }
    if (worktree) {
      if (t === null) repo.wtRemove(p);
      else {
        if (!same(repo.wtId(p), t)) repo.wtWrite(p, ...t);
        if (src !== null && !staged && sub === 'checkout') idx.set(p, repo.entryFor(p, ...t));
      }
      count++;
    }
  }
  repo.saveIndex([...idx.values(), ...otherEntries(unmerged)]);
  if (sub === 'checkout' && !quiet) {
    const key = source !== null ? (count === 1 ? 'updatedFromCommit' : 'updatedFromCommitMany') : (count === 1 ? 'updatedFromIndex' : 'updatedFromIndexMany');
    g.err(subMsg('checkout', key, { count: String(count), commit: abbrev(source ?? '') }) + '\n');
  }
  return 0;
}

function gitCheckout(g: Git, args: string[]): number {
  const o = parse('checkout', args);
  const repo = g.repo();
  const quiet = o.has('q'), force = o.has('f');
  const neu = o.val('b') || o.val('B');
  if (o.dashdash && neu === null) {
    if (o.args.length > 1) throw new GitError(msg('pathspecNoMatchKnown', { path: o.args[1] }), 1);
    const source = o.args.length ? commitOf(repo, o.args[0]) : null;
    if (!o.paths.length) return 0;
    return checkoutPaths(g, repo, source, o.paths, 'checkout', source !== null, true, true);
  }
  let target = o.args.length ? o.args[0] : null;
  if (target === '-') {
    const prev = previousBranch(repo, 1);
    if (prev === null) throw subFatal('checkout', 'noPreviousBranch');
    target = prev;
  }
  if (neu !== null) {
    if (!validBranchName(neu)) throw subFatal('branch', 'invalidName', { name: neu });
    const exists = repo.readRef('refs/heads/' + neu) !== null;
    if (exists && o.val('B') === null) throw subFatal('branch', 'exists', { name: neu });
    const start = target ? commitOf(repo, target) : repo.head();
    if (start === null && target === null) {
      repo.setHead('refs/heads/' + neu);
      if (!quiet) g.err(subMsg('checkout', 'switchedNewBranch', { branch: neu }) + '\n');
      return 0;
    }
    return moveHead(g, repo, 'refs/heads/' + neu, start, target || 'HEAD', force, quiet, neu, exists, false, remoteStart(repo, target));
  }
  if (target === null) {
    if (o.has('\x01')) return moveHead(g, repo, null, repo.head(), 'HEAD', force, quiet);
    return 0;
  }
  if (!o.has('\x01') && (target === 'HEAD' || target === '@') && repo.headRef() && o.args.length === 1)
    return moveHead(g, repo, repo.headRef(), repo.head(), repo.branch()!, force, quiet, null, false, true);
  if (!o.has('\x01') && repo.readRef('refs/heads/' + target) && o.args.length === 1)
    return moveHead(g, repo, 'refs/heads/' + target, repo.readRef('refs/heads/' + target), target, force, quiet);
  const sha = tryResolve(repo, target);
  const c = sha ? peel(repo, sha, 'commit') : null;
  if (c !== null) {
    if (o.args.length > 1) return checkoutPaths(g, repo, c, o.args.slice(1), 'checkout', true, true, quiet);
    return moveHead(g, repo, null, c, target, force, quiet);
  }
  if (o.has('\x01')) throw fatal('badRevision', { rev: target });
  const guess = o.args.length === 1 && !o.dashdash ? guessRemote(repo, target) : null;
  if (guess && isWorkPath(repo, target)) throw subFatal('checkout', 'fileOrBranch', { name: target, upstream: guess.slice(13) });
  if (guess) return moveHead(g, repo, 'refs/heads/' + target, repo.readRef(guess), guess.slice(13), force, quiet, target, false, false, guess);
  return checkoutPaths(g, repo, null, o.args, 'checkout', false, true, quiet);
}

function gitSwitch(g: Git, args: string[]): number {
  const o = parse('switch', args);
  const repo = g.repo();
  const quiet = o.has('q'), force = o.has('f');
  const neu = o.val('c') || o.val('C');
  let target = o.args.length ? o.args[0] : null;
  if (o.args.length > 1) throw usageError('switch', subMsg('switch', 'tooManyArguments'));
  if (target === '-') {
    target = previousBranch(repo, 1);
    if (target === null) throw subFatal('checkout', 'noPreviousBranch');
  }
  if (neu !== null) {
    if (!validBranchName(neu)) throw subFatal('branch', 'invalidName', { name: neu });
    const exists = repo.readRef('refs/heads/' + neu) !== null;
    if (exists && o.val('C') === null) throw subFatal('branch', 'exists', { name: neu });
    const start = target ? commitOf(repo, target) : repo.head();
    if (start === null) {
      repo.setHead('refs/heads/' + neu);
      if (!quiet) g.err(subMsg('checkout', 'switchedNewBranch', { branch: neu }) + '\n');
      return 0;
    }
    return moveHead(g, repo, 'refs/heads/' + neu, start, target || 'HEAD', force, quiet, neu, exists, false, remoteStart(repo, target));
  }
  if (o.has('d')) return moveHead(g, repo, null, commitOf(repo, target || 'HEAD'), target || 'HEAD', force, quiet);
  if (target === null) throw subFatal('switch', 'missingBranch');
  if (repo.readRef('refs/heads/' + target)) return moveHead(g, repo, 'refs/heads/' + target, repo.readRef('refs/heads/' + target), target, force, quiet);
  if ((target === 'HEAD' || target === '@') && repo.headRef()) throw subFatal('switch', 'expectedBranchRef', { rev: repo.headRef()! });
  if (tryResolve(repo, target)) throw subFatal('switch', 'expectedBranch', { rev: target });
  const guess = guessRemote(repo, target);
  if (guess) return moveHead(g, repo, 'refs/heads/' + target, repo.readRef(guess), guess.slice(13), force, quiet, target, false, false, guess);
  throw subFatal('switch', 'invalidReference', { rev: target });
}

function gitRestore(g: Git, args: string[]): number {
  const o = parse('restore', args);
  const repo = g.repo();
  const items = [...o.args, ...o.paths];
  if (!items.length) throw subFatal('restore', 'noPaths');
  const staged = o.has('S'), worktree = o.has('W') || !o.has('S');
  const src = o.val('s');
  const source = src ? commitOf(repo, src) : (staged ? repo.head() : null);
  if (staged && source === null && src === null) {
    // unborn branch: restoring the index means emptying it for those paths
    const specs = items.map(p => repo.fromUser(p));
    repo.saveIndex(repo.index().filter(e => !pathspecMatch(specs, e.path)));
    return 0;
  }
  return checkoutPaths(g, repo, source, items, 'restore', staged, worktree);
}

function gitReset(g: Git, args: string[]): number {
  const o = parse('reset', args);
  const repo = g.repo();
  const modes = ([['soft', '\x01'], ['mixed', '\x02'], ['hard', '\x03']] as const).filter(([, k]) => o.has(k)).map(([m]) => m as string);
  if (modes.length > 1) throw subFatal('reset', 'conflictingModes');
  const mode = modes.length ? modes[0] : 'mixed';
  let rev = 'HEAD', paths = [...o.paths];
  if (o.args.length) {
    if (tryResolve(repo, o.args[0]) !== null) { rev = o.args[0]; paths = [...o.args.slice(1), ...paths]; }
    else if (repo.head() === null && (o.args[0] === 'HEAD' || o.args[0] === '@')) paths = [...o.args.slice(1), ...paths];
    else if (!o.dashdash && o.args.every(a => repo.fs.exists(normalize(g.cwd, a), false))) paths = [...o.args, ...paths];
    else throw fatal('badRevision', { rev: o.args[0] });
  }
  const head = repo.head();
  const target = (rev === 'HEAD' || rev === '@') && head === null ? null : commitOf(repo, rev);
  if (paths.length) {
    if (mode !== 'mixed') throw subFatal('reset', 'pathsWithMode', { mode });
    const specs = paths.map(p => repo.fromUser(p));
    const src = treeFiles(repo, target);
    const entries = repo.index();
    const idx = repo.stage0(entries);
    for (const p of union(src.keys(), idx.keys(), repo.unmerged(entries).keys())) {
      if (!pathspecMatch(specs, p)) continue;
      const t = src.get(p);
      if (!t) idx.delete(p);
      else if (!idx.has(p) || !same([idx.get(p)!.mode, idx.get(p)!.sha], t)) idx.set(p, new Entry(p, t[1], t[0]));
    }
    const keep = entries.filter(e => e.stage && !pathspecMatch(specs, e.path));
    repo.saveIndex([...idx.values(), ...keep]);
    unstagedReport(g, repo, o.has('q'));
    return 0;
  }
  if (head) repo.gwrite('ORIG_HEAD', head + '\n');
  if (mode === 'hard') switchTrees(g, repo, head, target, true);
  else if (mode === 'mixed') {
    const src = treeFiles(repo, target);
    const old = repo.stage0();
    repo.saveIndex([...src].map(([p, t]) => { const e = old.get(p); return e && e.mode === t[0] && e.sha === t[1] ? e : new Entry(p, t[1], t[0]); }));
  }
  if (target !== null) {
    repo.updateRef('HEAD', target, `reset: moving to ${rev}`);
    if (!repo.headRef()) repo.gwrite('HEAD', target + '\n');
  }
  for (const f of ['MERGE_HEAD', 'MERGE_MSG', 'MERGE_MODE']) repo.gremove(f);
  if (mode === 'hard' && !o.has('q') && target) g.out(subMsg('reset', 'headNowAt', { commit: subjectOf(repo, target) }) + '\n');
  if (mode === 'mixed') unstagedReport(g, repo, o.has('q'));
  return 0;
}

function unstagedReport(g: Git, repo: Repo, quiet: boolean): void {
  if (quiet) return;
  const idx = indexFiles(repo.index());
  const lines: string[] = [];
  for (const p of sortCp(idx.keys())) {
    const w = repo.wtId(p);
    if (w === null) lines.push(`D\t${repo.show(p)}`);
    else if (!same(w, idx.get(p))) lines.push(`M\t${repo.show(p)}`);
  }
  if (lines.length) g.out(subMsg('reset', 'unstagedAfterReset') + '\n' + lines.join('\n') + '\n');
}

// ---------- branch / tag ----------

function gitBranch(g: Git, args: string[]): number {
  const o = parse('branch', args);
  const repo = g.repo();
  const M = (key: string, v: Values = {}) => subMsg('branch', key, v);
  if (o.has('\x01')) {
    const b = repo.branch();
    if (b !== null) g.out(b + '\n');
    return 0;
  }
  if (o.has('d') || o.has('D')) {
    if (!o.args.length) throw subFatal('branch', 'nameRequired');
    let code = 0;
    const head = repo.head();
    let reach: Set<string> | null = null;
    for (const name of o.args) {
      const sha = repo.readRef('refs/heads/' + name);
      if (sha === null) { g.err(M('notFound', { name }) + '\n'); code = 1; continue; }
      if (repo.headRef() === 'refs/heads/' + name) { g.err(M('checkedOut', { name, path: repo.top }) + '\n'); code = 1; continue; }
      if (!(o.has('D') || o.has('f')) && (head === null || !(reach ??= ancestors(repo, [head], g.check)).has(sha))) {
        g.err(M('notMerged', { name }) + '\n');
        code = 1;
        continue;
      }
      repo.deleteRef('refs/heads/' + name);
      g.out(M('deleted', { name, commit: abbrev(sha) }) + '\n');
    }
    return code;
  }
  if (o.has('m') || o.has('M')) {
    const names = o.args;
    let old: string | null, neu: string;
    if (names.length === 1) [old, neu] = [repo.branch(), names[0]];
    else if (names.length === 2) [old, neu] = [names[0], names[1]];
    else throw subFatal('branch', 'renameArgs');
    if (old === null) throw subFatal('branch', 'renameDetached');
    if (!validBranchName(neu)) throw subFatal('branch', 'invalidName', { name: neu });
    const sha = repo.readRef('refs/heads/' + old);
    if (sha === null && repo.headRef() !== 'refs/heads/' + old) throw subFatal('branch', 'noSuchBranch', { name: old });
    if (repo.readRef('refs/heads/' + neu) && neu !== old) {
      if (!o.has('M')) throw subFatal('branch', 'exists', { name: neu });
      if (repo.headRef() === 'refs/heads/' + neu) throw subFatal('branch', 'forceCurrent', { name: neu });
    }
    if (neu === old) return 0;
    const current = repo.headRef() === 'refs/heads/' + old;
    const nested = neu.startsWith(old + '/') || old.startsWith(neu + '/');
    const log = sha !== null ? repo.gread('logs/refs/heads/' + old) : null;
    if (sha !== null && nested) repo.deleteRef('refs/heads/' + old); // one name is a directory of the other: the old one has to go first
    // otherwise the new name and HEAD are written before the old name goes, so a failed write loses nothing
    if (sha !== null) {
      repo.gwrite('refs/heads/' + neu, sha + '\n');
      if (log && log.length) repo.gwrite('logs/refs/heads/' + neu, log);
    }
    if (current) repo.gwrite('HEAD', `ref: refs/heads/${neu}\n`);
    if (sha !== null && !nested) repo.deleteRef('refs/heads/' + old);
    return 0;
  }
  const listing = o.has('\x02') || o.has('a') || o.has('r') || o.has('v') || !o.args.length;
  if (listing) {
    const pattern = o.has('\x02') && o.args.length ? o.args.map(p => globFull(p, false)) : null;
    const rows: [mark: string, name: string, sha: string | null, sym: string | null, local: string | null][] = [];
    if (!o.has('r') || o.has('a')) {
      const headRef = repo.headRef();
      if (headRef === null && repo.head() && !pattern) {
        const [at, label] = detachedName(repo);
        rows.push(['*', M(at ? 'detachedLabel' : 'detachedFromLabel', { commit: label }), repo.head()!, null, null]);
      }
      for (const [name, sha] of repo.refs('refs/heads/')) {
        const short = name.slice(11);
        if (pattern && !pattern.some(p => p?.test(short))) continue;
        rows.push([name === headRef ? '*' : ' ', short, sha, null, short]);
      }
    }
    if (o.has('r') || o.has('a')) {
      const prefix = o.has('a') ? 'remotes/' : '';
      for (const [short, sha, sym] of remoteBranches(repo)) {
        if (pattern && !pattern.some(p => p?.test(short) || p?.test(prefix + short))) continue;
        rows.push([' ', prefix + short, sha, sym, null]);
      }
    }
    const width = Math.max(0, ...rows.map(r => cpLen(r[1])));
    for (const [mark, name, sha, sym, local] of rows) {
      if (sym !== null) g.out(o.has('v') ? `${mark} ${ljust(name, width)} -> ${sym}\n` : `${mark} ${name} -> ${sym}\n`);
      else if (o.has('v') && sha) {
        const up = local && (o.flags.get('v') ?? 0) > 1 ? trackingBrief(repo, local, g.check) : null;
        const track = up === null ? '' : `[${up[0]}${up[1] ? ': ' + up[1] : ''}] `;
        g.out(`${mark} ${ljust(name, width)} ${abbrev(sha)} ${track}${repo.commit(sha).subject}\n`);
      } else g.out(`${mark} ${name}\n`);
    }
    return 0;
  }
  // create
  const name = o.args[0];
  if (o.args.length > 2) throw subFatal('branch', 'tooManyArguments');
  if (!validBranchName(name)) throw subFatal('branch', 'invalidName', { name });
  const exists = repo.readRef('refs/heads/' + name);
  if (exists && !o.has('f')) throw subFatal('branch', 'exists', { name });
  if (exists && o.has('f') && repo.headRef() === 'refs/heads/' + name) throw subFatal('branch', 'forceCurrent', { name });
  const start = o.args.length > 1 ? o.args[1] : 'HEAD';
  let sha = tryResolve(repo, start);
  sha = sha ? peel(repo, sha, 'commit') : null;
  if (sha === null) throw subFatal('branch', 'notValidObject', { name: start === 'HEAD' ? (repo.branch() || 'HEAD') : start });
  repo.updateRef('refs/heads/' + name, sha, `branch: Created from ${start}`);
  const track = remoteStart(repo, start);
  if (track) setTracking(g, repo, name, track, false);
  return 0;
}

function gitTag(g: Git, args: string[]): number {
  const o = parse('tag', args);
  const repo = g.repo();
  const M = (key: string, v: Values = {}) => subMsg('tag', key, v);
  if (o.has('d')) {
    let code = 0;
    for (const name of o.args) {
      const sha = repo.readRef('refs/tags/' + name);
      if (sha === null) { g.err(M('notFound', { name }) + '\n'); code = 1; continue; }
      repo.deleteRef('refs/tags/' + name);
      g.out(M('deleted', { name, commit: abbrev(sha) }) + '\n');
    }
    return code;
  }
  if (o.has('l') || !o.args.length) {
    const pats = o.args.map(p => globFull(p, false));
    for (const [name] of repo.refs('refs/tags/')) {
      const short = name.slice(10);
      if (!pats.length || pats.some(p => p?.test(short))) g.out(short + '\n');
    }
    return 0;
  }
  const name = o.args[0];
  if (o.args.length > 2) throw usageError('tag', M('tooManyArguments'));
  if (!validBranchName(name)) throw subFatal('tag', 'invalidName', { name });
  const old = repo.readRef('refs/tags/' + name);
  if (old && !o.has('f')) throw subFatal('tag', 'exists', { name });
  let target = o.args.length > 1 ? resolve(repo, o.args[1]) : repo.head();
  if (target === null) throw fatal('badRevision', { rev: 'HEAD' });
  if (o.has('a') || o.vals('m').length || o.val('F') !== null) {
    let text: string;
    if (o.vals('m').length) text = o.vals('m').join('\n\n');
    else if (o.val('F') !== null) text = readMessageFile(g, o.val('F')!);
    else text = g.editorError();
    text = cleanMessage(text);
    const kind = repo.obj(target)[0];
    target = repo.put('tag', `object ${target}\ntype ${kind}\ntag ${name}\ntagger ${g.ident('committer')}\n\n${text}`);
  }
  repo.updateRef('refs/tags/' + name, target);
  if (old && old !== target) g.out(M('updated', { name, commit: abbrev(old) }) + '\n');
  return 0;
}

// ---------- merge ----------

function mergeLabel(repo: Repo, arg: string): string {
  if (repo.readRef('refs/heads/' + arg)) return `branch '${arg}'`;
  if (repo.readRef('refs/tags/' + arg)) return `tag '${arg}'`;
  return `commit '${arg}'`;
}

/** Merges theirs into ours (the index and work tree hold ours). */
function threeWay(g: Git, repo: Repo, base: string | null, ours: string, theirs: string, theirLabel: string, outLines: string[]):
  [Files, string[], Entry[], Map<string, [string, Uint8Array]>] {
  const B = treeFiles(repo, base), O = treeFiles(repo, ours), T = treeFiles(repo, theirs);
  const result: Files = new Map();
  const stages: Entry[] = [];
  const conflicts: string[] = [];
  const worktree = new Map<string, [string, Uint8Array]>();
  const addStages = (p: string, b?: F, o?: F, t?: F) => {
    for (const [n, side] of [[1, b], [2, o], [3, t]] as [number, F | undefined][]) if (side) stages.push(new Entry(p, side[1], side[0], n));
  };
  for (const p of sortPaths(union(B.keys(), O.keys(), T.keys()))) {
    g.ctx.checkTime();
    const b = B.get(p), o = O.get(p), t = T.get(p);
    if (same(o, t)) { if (o) result.set(p, o); continue; }
    if (same(b, o)) { if (t) result.set(p, t); continue; }
    if (same(b, t)) { if (o) result.set(p, o); continue; }
    if (o && t && o[0] !== '120000' && t[0] !== '120000') {
      const od = repo.typed(o[1], 'blob'), td = repo.typed(t[1], 'blob');
      const bd = b ? repo.typed(b[1], 'blob') : new Uint8Array(0);
      outLines.push(msg('autoMerging', { path: p }));
      const mode = o[0] === t[0] ? o[0] : (b && b[0] === o[0] ? t[0] : o[0]);
      let merged: Uint8Array, conflict: boolean;
      if (isBinary(od) || isBinary(td) || isBinary(bd)) {
        outLines.push(msg('binaryConflict', { path: p, ours: 'HEAD', theirs: theirLabel }));
        [merged, conflict] = [od, true];
      } else [merged, conflict] = mergeLines(bd, od, td, 'HEAD', theirLabel, g.check);
      if (!conflict) { result.set(p, [mode, repo.put('blob', merged)]); continue; }
      outLines.push(msg(b ? 'conflictContent' : 'conflictAddAdd', { path: p }));
      conflicts.push(p);
      addStages(p, b, o, t);
      worktree.set(p, [mode, merged]);
      continue;
    }
    // deleted on one side, changed on the other (or a symbolic link changed both ways)
    let keep: F;
    if (o && t) { outLines.push(msg('conflictContent', { path: p })); keep = o; }
    else if (o) { outLines.push(msg('conflictModifyDelete', { path: p, deleted: theirLabel, modified: 'HEAD' })); keep = o; }
    else { outLines.push(msg('conflictModifyDelete', { path: p, deleted: 'HEAD', modified: theirLabel })); keep = t!; }
    conflicts.push(p);
    addStages(p, b, o, t);
    worktree.set(p, [keep[0], repo.typed(keep[1], 'blob')]);
  }
  return [result, conflicts, stages, worktree];
}

function gitMerge(g: Git, args: string[]): number {
  const o = parse('merge', args);
  const repo = g.repo();
  const M = (key: string, v: Values = {}) => subMsg('merge', key, v);
  const clearMerge = () => { for (const f of ['MERGE_HEAD', 'MERGE_MSG', 'MERGE_MODE']) repo.gremove(f); };
  if (o.has('\x04')) { // --abort
    if (!repo.gexists('MERGE_HEAD')) throw subFatal('merge', 'noMergeToAbort');
    switchTrees(g, repo, repo.head(), repo.head(), true);
    clearMerge();
    return 0;
  }
  if (o.has('\x05')) { // --continue
    if (!repo.gexists('MERGE_HEAD')) throw subFatal('merge', 'noMergeInProgress');
    return gitCommit(g, ['--no-edit']);
  }
  const entries = repo.index();
  if (repo.unmerged(entries).size) throw new GitError(M('unmergedFiles'), 128);
  if (repo.gexists('MERGE_HEAD')) throw new GitError(M('mergeHeadExists'), 128);
  if (!o.args.length) throw subFatal('merge', 'noRemote');
  if (o.args.length > 1) throw subFatal('merge', 'octopus');
  const arg = o.args[0];
  const sha = tryResolve(repo, arg);
  const theirs = sha ? peel(repo, sha, 'commit') : null;
  if (theirs === null) throw new GitError(M('notSomething', { name: arg }), 1);
  const head = repo.head();
  const quiet = o.has('q');
  if (head === null) {
    switchTrees(g, repo, null, theirs, false, 'merge');
    repo.updateRef('HEAD', theirs, `merge ${arg}: Fast-forward`);
    return 0;
  }
  if (theirs === head || ancestors(repo, [head], g.check).has(theirs)) {
    if (!quiet) g.out(M('upToDate') + '\n');
    return 0;
  }
  const base = mergeBase(repo, head, theirs, g.check);
  const ff = base === head;
  if (o.has('\x02') && !ff) throw subFatal('merge', 'notFastForward');
  const cur = repo.branch();
  const label = mergeLabel(repo, arg);
  const message = o.vals('m').length ? o.vals('m').join('\n\n') : `Merge ${label}` + (cur && cur !== 'master' && cur !== 'main' ? ` into ${cur}` : '');
  if (ff && !o.has('\x01')) {
    if (!quiet) g.out(M('updating', { old: abbrev(head), new: abbrev(theirs) }) + '\n');
    switchTrees(g, repo, head, theirs, false, 'merge');
    repo.gwrite('ORIG_HEAD', head + '\n');
    repo.updateRef('HEAD', theirs, `merge ${arg}: Fast-forward`);
    if (!quiet) { g.out(M('fastForward') + '\n'); printDiffstat(g, repo, head, theirs); }
    return 0;
  }
  // a real merge: refuse when the index has staged changes, or local changes touch merged paths
  const idx = indexFiles(entries);
  const headFiles = treeFiles(repo, head);
  const all = sortCp(union(idx.keys(), headFiles.keys()));
  if (all.some(p => !same(idx.get(p), headFiles.get(p)))) {
    const dirty = all.filter(p => !same(idx.get(p), headFiles.get(p)));
    throw new GitError(subMsg('checkout', 'localChanges', {
      verb: 'merge', paths: rstripChars(dirty.map(p => `\t${p}\n`).join(''), '\n'), advice: subMsg('checkout', 'localChangesAdviceMerge'),
    }), 2);
  }
  const lines: string[] = [];
  const [result, conflicts, stages, worktree] = threeWay(g, repo, base, head, theirs, arg, lines);
  const touched = [...sortCp(union(result.keys(), headFiles.keys())).filter(p => !same(result.get(p), headFiles.get(p))), ...conflicts];
  const dirty = touched.filter(p => idx.has(p) && !same(repo.wtId(p), idx.get(p)));
  const clobber = touched.filter(p => !idx.has(p) && repo.wtId(p) !== null && !repo.ignored(p));
  if (dirty.length) {
    g.err(subMsg('checkout', 'localChanges', {
      verb: 'merge', paths: rstripChars(dirty.map(p => `\t${p}\n`).join(''), '\n'), advice: subMsg('checkout', 'localChangesAdviceMerge'),
    }) + '\n');
    g.out(M('strategyFailed') + '\n');
    return 2;
  }
  if (clobber.length) {
    throw new GitError(subMsg('checkout', 'untrackedOverwritten', {
      verb: 'merge', paths: rstripChars(clobber.map(p => `\t${p}\n`).join(''), '\n'), advice: subMsg('checkout', 'untrackedAdviceMerge'),
    }), 2);
  }
  for (const line of lines) g.out(line + '\n');
  repo.gwrite('ORIG_HEAD', head + '\n');
  // write the result into the work tree and index
  const newEntries: Entry[] = [];
  for (const p of sortCp(union(headFiles.keys(), result.keys(), worktree.keys()))) {
    const w = worktree.get(p);
    if (w) {
      const [mode, data] = w;
      repo.wtWrite(p, mode !== '120000' ? mode : '100644', repo.put('blob', data));
      continue;
    }
    const t = result.get(p);
    if (!t) repo.wtRemove(p);
    else {
      if (!same(headFiles.get(p), t) || !same(repo.wtId(p), t)) repo.wtWrite(p, ...t);
      newEntries.push(repo.entryFor(p, ...t));
    }
  }
  repo.saveIndex([...newEntries, ...stages]);
  if (conflicts.length) {
    repo.gwrite('MERGE_HEAD', theirs + '\n');
    repo.gwrite('MERGE_MODE', o.has('\x01') ? 'no-ff\n' : '');
    repo.gwrite('MERGE_MSG', message + '\n\n# Conflicts:\n' + conflicts.map(p => `#\t${p}\n`).join(''));
    g.out(M('failed') + '\n');
    return 1;
  }
  const tree = repo.writeTree(result);
  const body = `tree ${tree}\nparent ${head}\nparent ${theirs}\nauthor ${g.ident('author')}\ncommitter ${g.ident('committer')}\n\n` + cleanMessage(message);
  const made = repo.put('commit', body);
  repo.updateRef('HEAD', made, `merge ${arg}: Merge made by the 'ort' strategy.`);
  if (!quiet) { g.out(M('madeByOrt') + '\n'); printDiffstat(g, repo, head, made); }
  return 0;
}

function printDiffstat(g: Git, repo: Repo, old: string, neu: string): void {
  const a = treeFiles(repo, old), b = treeFiles(repo, neu);
  const changes = diffFiles(a, b, null, treeLoader(repo, a, b));
  if (changes.length) writeStat(s => g.out(s), statEntries(g, repo, a, b, changes));
  for (const line of summaryLines(changes, a, b)) g.out(line + '\n');
}

// ---------- stash ----------

/** [commit, message][], newest first, from the stash's reflog. */
function stashEntries(repo: Repo): [string, string][] {
  const out: [string, string][] = [];
  for (const line of (repo.gtext('logs/refs/stash') ?? '').split('\n')) {
    const m = /^[0-9a-f]{40} ([0-9a-f]{40}) [^\t]*\t(.*)$/.exec(line);
    if (m) out.push([m[1], m[2]]);
  }
  return out.reverse();
}

function writeStash(repo: Repo, entries: [string, string][]): void {
  if (!entries.length) { repo.gremove('refs/stash'); repo.gremove('logs/refs/stash'); return; }
  const lines: string[] = [];
  let prev = '0'.repeat(40);
  const who = repo.g.ident('committer', false);
  for (const [sha, message] of [...entries].reverse()) { lines.push(`${prev} ${sha} ${who}\t${message}\n`); prev = sha; }
  repo.gwrite('logs/refs/stash', lines.join(''));
  repo.gwrite('refs/stash', entries[0][0] + '\n');
}

function stashRef(repo: Repo, arg: string | null): [number, string] {
  const entries = stashEntries(repo);
  if (!entries.length) throw new GitError(subMsg('stash', 'noEntries'), 1);
  let n = 0;
  if (arg !== null) {
    const m = /^(?:stash@\{)?(\d+)\}?$/.exec(arg);
    if (!m) throw new GitError(subMsg('stash', 'notStash', { name: arg }), 1);
    n = Number(m[1]);
  }
  if (n >= entries.length) throw new GitError(subMsg('stash', 'noSuchEntry', { name: `stash@{${n}}` }), 1);
  return [n, entries[n][0]];
}

function gitStash(g: Git, args: string[]): number {
  let action = 'push';
  if (args.length && !args[0].startsWith('-')) { action = args[0]; args = args.slice(1); }
  if (!['push', 'save', 'list', 'show', 'pop', 'apply', 'drop', 'clear'].includes(action)) throw usageError('stash', subMsg('stash', 'unknownAction', { action }));
  const o = parse('stash', args);
  const repo = g.repo();
  const M = (key: string, v: Values = {}) => subMsg('stash', key, v);
  if (action === 'push' || action === 'save') {
    const head = repo.head();
    if (head === null) throw new GitError(M('noInitialCommit'), 1);
    if (repo.unmerged().size) throw new GitError(M('unmerged'), 1);
    const given = o.vals('m').length ? o.vals('m').join('\n\n') : (action === 'save' && o.args.length ? o.args.join(' ') : null);
    const st = new Status(repo, o.has('u') ? 'all' : 'no');
    const items = [...o.args, ...o.paths];
    const specs = action === 'push' ? userSpecs(repo, items) : null;
    const headFiles = treeFiles(repo, head);
    if (specs !== null) {
      const known = union(indexFiles(repo.index()).keys(), headFiles.keys(), st.untracked);
      items.forEach((raw, k) => {
        if (![...known].some(p => pathspecMatch([specs[k]], p))) throw new GitError(msg('pathspecNoMatchKnown', { path: raw }), 1);
      });
      st.staged = st.staged.filter(c => pathspecMatch(specs, (c[2] ?? c[1])!) || (c[1] !== null && pathspecMatch(specs, c[1])));
      st.unstaged = st.unstaged.filter(c => pathspecMatch(specs, c[1]!));
      st.untracked = st.untracked.filter(u => pathspecMatch(specs, u));
    }
    if (!st.staged.length && !st.unstaged.length && !(o.has('u') && st.untracked.length)) {
      if (!o.has('q')) g.out(M('noLocalChanges') + '\n');
      return 0;
    }
    const b = repo.branch() || '(no branch)';
    const desc = `${b}: ${subjectOf(repo, head)}`;
    const message = given ? M('onMessage', { branch: b, message: given }) : M('wipMessage', { description: desc });
    const ident = g.ident('committer');
    const author = g.ident('author');
    let idxFiles = indexFiles(repo.index());
    let chosen = new Set<string>();
    if (specs !== null) { // only the named paths change: everything else stays as HEAD has it
      chosen = new Set([...union(idxFiles.keys(), headFiles.keys())].filter(p => pathspecMatch(specs, p)));
      idxFiles = new Map([...[...headFiles].filter(([p]) => !chosen.has(p)), ...[...idxFiles].filter(([p]) => chosen.has(p))]);
    }
    const itree = repo.writeTree(idxFiles);
    const icommit = repo.put('commit', `tree ${itree}\nparent ${head}\nauthor ${author}\ncommitter ${ident}\n\n${M('indexMessage', { description: desc })}\n`);
    const work: Files = new Map(idxFiles);
    let k = 0;
    for (const p of idxFiles.keys()) {
      if (k++ % 256 === 255) g.ctx.checkTime();
      if (specs !== null && !chosen.has(p)) continue;
      const w = repo.wtStat(p);
      if (w === null) work.delete(p);
      else work.set(p, [w[0], repo.put('blob', w[1])]);
    }
    const parents = [head, icommit];
    if (o.has('u') && st.untracked.length) {
      const uf: Files = new Map();
      st.untracked.forEach((p, n) => {
        if (n % 256 === 255) g.ctx.checkTime();
        const w = repo.wtStat(p);
        if (w) uf.set(p, [w[0], repo.put('blob', w[1])]);
      });
      const utree = repo.writeTree(uf);
      parents.push(repo.put('commit', `tree ${utree}\nauthor ${author}\ncommitter ${ident}\n\n${M('untrackedMessage', { description: desc })}\n`));
    }
    const wtree = repo.writeTree(work);
    const wcommit = repo.put('commit', `tree ${wtree}\n` + parents.map(p => `parent ${p}\n`).join('') + `author ${author}\ncommitter ${ident}\n\n${message}`);
    writeStash(repo, [[wcommit, message], ...stashEntries(repo)]);
    if (specs === null) switchTrees(g, repo, head, head, true);
    else {
      const byPath = repo.stage0();
      for (const p of sortCp(chosen)) {
        const h = headFiles.get(p);
        if (!h) { byPath.delete(p); repo.wtRemove(p); continue; }
        if (!same(repo.wtId(p), h)) repo.wtWrite(p, ...h);
        byPath.set(p, repo.entryFor(p, ...h));
      }
      repo.saveIndex(byPath.values());
    }
    if (o.has('u')) for (const p of st.untracked) repo.wtRemove(p);
    if (!o.has('q')) g.out(M('saved', { message }) + '\n');
    return 0;
  }
  if (action === 'list') {
    stashEntries(repo).forEach(([, message], i) => g.out(`stash@{${i}}: ${message}\n`));
    return 0;
  }
  if (action === 'clear') { writeStash(repo, []); return 0; }
  const arg = o.args.length ? o.args[0] : null;
  const [n, sha] = stashRef(repo, arg);
  if (action === 'show') {
    const c = repo.commit(sha);
    const a = treeFiles(repo, c.parents[0]), b = repo.flatten(c.tree);
    if (!(o.has('p') || o.has('\x03') || o.has('\x04'))) o.flags.set('\x01', 1);
    writeDiff(g, repo, a, b, o);
    return 0;
  }
  if (action === 'drop') return stashDrop(g, repo, n, sha, o.has('q'), arg);
  // apply / pop
  const c = repo.commit(sha);
  const base = c.parents[0];
  const lines: string[] = [];
  const stashFiles = repo.flatten(c.tree), baseFiles = treeFiles(repo, base);
  const untrackedFiles = c.parents.length > 2 ? repo.flatten(repo.commit(c.parents[2]).tree) : new Map<string, F>();
  for (const p of untrackedFiles.keys()) if (repo.wtStat(p) !== null) throw new GitError(M('untrackedExists', { path: p }), 1);
  const entries = repo.index();
  const idx = repo.stage0(entries);
  const cur = indexFiles(entries);
  const paths = sortCp(union(stashFiles.keys(), baseFiles.keys()));
  const workNow = new Map(paths.map(p => [p, repo.wtId(p)]));
  const dirty = paths.filter(p => !same(stashFiles.get(p), baseFiles.get(p)) && cur.has(p) && !same(workNow.get(p), cur.get(p)));
  if (dirty.length) {
    throw new GitError(subMsg('checkout', 'localChanges', {
      verb: 'merge', paths: rstripChars(dirty.map(p => `\t${p}\n`).join(''), '\n'), advice: subMsg('checkout', 'localChangesAdviceMerge'),
    }) + '\n' + M('cannotApply'), 1);
  }
  const conflicts: string[] = [];
  if (c.tree === repo.commit(base).tree) g.out(M('upToDate') + '\n');
  for (const p of paths) {
    const s = stashFiles.get(p), b = baseFiles.get(p);
    if (same(s, b)) continue;
    const mine = cur.get(p);
    if (same(mine, b) || same(mine, s)) {
      if (!s) { repo.wtRemove(p); idx.delete(p); }
      else {
        repo.wtWrite(p, ...s);
        if (!b) idx.set(p, repo.entryFor(p, ...s));
      }
      continue;
    }
    if (!s || !mine) {
      const up = M('labelUpstream'), stashed = M('labelStashed');
      lines.push(msg('conflictModifyDelete', { path: p, deleted: !mine ? up : stashed, modified: !mine ? stashed : up }));
      conflicts.push(p);
      continue;
    }
    lines.push(msg('autoMerging', { path: p }));
    const [merged, conflict] = mergeLines(b ? repo.typed(b[1], 'blob') : new Uint8Array(0), repo.typed(mine[1], 'blob'), repo.typed(s[1], 'blob'),
      M('labelUpstream'), M('labelStashed'), g.check);
    repo.wtWrite(p, s[0], repo.put('blob', merged));
    if (conflict) {
      lines.push(msg('conflictContent', { path: p }));
      conflicts.push(p);
      idx.delete(p);
      for (const [k, side] of [[1, b], [2, mine], [3, s]] as [number, F | undefined][]) if (side) entries.push(new Entry(p, side[1], side[0], k));
    }
  }
  for (const [p, t] of untrackedFiles) repo.wtWrite(p, ...t);
  repo.saveIndex([...idx.values(), ...entries.filter(e => e.stage && conflicts.includes(e.path))]);
  for (const line of lines) g.out(line + '\n');
  if (conflicts.length) {
    g.out(longStatus(repo, new Status(repo)));
    if (action === 'pop') g.out(M('keptEntry') + '\n');
    return 1;
  }
  if (!o.has('q')) g.out(longStatus(repo, new Status(repo)));
  if (action === 'pop') return stashDrop(g, repo, n, sha, o.has('q'), arg);
  return 0;
}

function stashDrop(g: Git, repo: Repo, n: number, sha: string, quiet: boolean, given: string | null = null): number {
  const entries = stashEntries(repo);
  entries.splice(n, 1);
  writeStash(repo, entries);
  if (!quiet) g.out(subMsg('stash', 'dropped', { name: given || `refs/stash@{${n}}`, commit: sha }) + '\n');
  return 0;
}

// ---------- plumbing and inspection ----------

function gitRevParse(g: Git, args: string[]): number {
  let short: number | null = null, abbrevRef = false, verify = false, quiet = false, symbolic = false;
  const outputs: string[] = [];
  let revs = 0;
  const flushOut = () => outputs.map(x => x + '\n').join('');
  for (const a of args) {
    if (['--is-inside-work-tree', '--is-inside-git-dir', '--show-toplevel', '--git-dir', '--show-prefix', '--absolute-git-dir', '--show-cdup',
      '--is-bare-repository', '--git-common-dir'].includes(a)) {
      const r = g.repo();
      outputs.push(({
        '--is-inside-work-tree': 'true', '--is-inside-git-dir': 'false', '--show-toplevel': r.top, '--git-dir': !r.prefix ? '.git' : r.git,
        '--absolute-git-dir': r.git, '--git-common-dir': !r.prefix ? '.git' : r.git, '--show-prefix': r.prefix, '--is-bare-repository': 'false',
        '--show-cdup': '../'.repeat(r.prefix.split('/').length - 1),
      } as Values)[a]);
      continue;
    }
    const m = /^--short(?:=(\d+))?$/.exec(a);
    if (m) { short = Math.max(4, Math.min(40, Number(m[1] ?? 7))); continue; }
    if (a === '--abbrev-ref') { abbrevRef = true; continue; }
    if (a === '--symbolic-full-name') { symbolic = true; continue; }
    if (a === '--verify') { verify = true; continue; }
    if (a === '-q' || a === '--quiet') { quiet = true; continue; }
    if (a === '--') continue;
    if (a.startsWith('-')) { outputs.push(a); continue; }
    const r = g.repo();
    revs++;
    const sha = tryResolve(r, a);
    if (sha === null) {
      if (verify) {
        if (quiet) { g.out(flushOut()); return 1; }
        throw subFatal('rev-parse', 'needSingleRevision');
      }
      g.err(msg('badRevision', { rev: a }) + '\n');
      g.out(flushOut() + a + '\n');
      return 128;
    }
    if (abbrevRef || symbolic) {
      const prev = /^@\{-(\d+)\}$/.exec(a);
      if (prev && abbrevRef) { outputs.push(previousBranch(r, Number(prev[1])) || a); continue; }
      let name: string | null = null;
      const upOf = /^(.*)@\{(?:u|upstream)\}$/s.exec(a);
      if (upOf) {
        const up = upstream(r, upstreamBranch(r, upOf[1]));
        name = up ? (abbrevRef ? up[0] : up[1]) : null;
      } else if (a === 'HEAD' || a === '@') {
        const ref = r.headRef();
        name = ref ? (abbrevRef && ref.startsWith('refs/heads/') ? ref.slice(11) : ref) : 'HEAD';
      } else if (r.readRef('refs/heads/' + a)) name = abbrevRef ? a : 'refs/heads/' + a;
      else if (r.readRef('refs/tags/' + a)) name = abbrevRef ? a : 'refs/tags/' + a;
      else if (r.readRef('refs/remotes/' + a)) name = abbrevRef ? a : 'refs/remotes/' + a;
      outputs.push(name !== null ? name : (abbrevRef ? a : ''));
      continue;
    }
    outputs.push(short ? sha.slice(0, short) : sha);
  }
  if (verify && revs !== 1) throw subFatal('rev-parse', 'needSingleRevision');
  g.out(flushOut());
  return 0;
}

function gitLsFiles(g: Git, args: string[]): number {
  const o = parse('ls-files', args);
  const repo = g.repo();
  let specs = userSpecs(repo, [...o.args, ...o.paths]);
  if (specs === null && repo.prefix) specs = [rstripChars(repo.prefix, '/')];
  if (o.has('\x02')) repo.prefix = '';
  const entries = repo.index();
  let out: string[] = [];
  const staged = (e: Entry) => `${e.mode} ${e.sha} ${e.stage}\t${cQuote(repo.show(e.path))}`;
  const modes = ['c', 'o', 'm', 'd', 'u', 's'].filter(k => o.has(k));
  if (!modes.length || o.has('c') || (o.has('s') && !o.has('o'))) {
    for (const e of entries) {
      if (pathspecMatch(specs, e.path) && (!o.has('u') || e.stage)) out.push(o.has('s') || o.has('u') ? staged(e) : cQuote(repo.show(e.path)));
    }
    if (!o.has('s') && !o.has('u')) out = [...new Set(out)];
  }
  if (o.has('u') && !o.has('c') && !o.has('s')) out = entries.filter(e => e.stage && pathspecMatch(specs, e.path)).map(staged);
  if (o.has('d') || o.has('m')) {
    for (const [p, f] of indexFiles(entries)) {
      if (!pathspecMatch(specs, p)) continue;
      const w = repo.wtId(p);
      if ((o.has('d') && w === null) || (o.has('m') && !same(w, f))) out.push(cQuote(repo.show(p)));
    }
  }
  if (o.has('o')) {
    const tracked = new Set(entries.map(e => e.path));
    for (const p of repo.walk('')) {
      if (tracked.has(p) || !pathspecMatch(specs, rstripChars(p, '/'))) continue;
      if (o.has('\x01') && repo.ignored(rstripChars(p, '/'), p.endsWith('/'))) continue;
      out.push(cQuote(repo.show(p)));
    }
    out = sortPaths(out);
  }
  for (const line of out) g.out(line + '\n');
  return 0;
}

function gitCatFile(g: Git, args: string[]): number {
  const o = parse('cat-file', args);
  const repo = g.repo();
  const modes = ['t', 's', 'p', 'e'].filter(k => o.has(k));
  if (modes.length > 1 || (!modes.length && o.args.length !== 2) || (modes.length && o.args.length !== 1)) throw usageError('cat-file', subMsg('cat-file', 'badUsage'));
  const name = o.args[o.args.length - 1];
  const sha = tryResolve(repo, name);
  if (sha === null) {
    if (modes.length === 1 && modes[0] === 'e') return 1;
    throw subFatal('cat-file', 'notValid', { name });
  }
  const [kind, data] = repo.obj(sha);
  if (!modes.length) {
    const want = o.args[0];
    if (!['blob', 'tree', 'commit', 'tag'].includes(want)) throw subFatal('cat-file', 'badType', { kind: want });
    const sha2 = peel(repo, sha, want);
    if (sha2 === null) throw subFatal('cat-file', 'badFile', { name });
    g.out(decodeBytes(repo.obj(sha2)[1]));
    return 0;
  }
  if (o.has('e')) return 0;
  if (o.has('t')) g.out(kind + '\n');
  else if (o.has('s')) g.out(`${data.length}\n`);
  else if (kind === 'tree') {
    for (const [mode, n, s] of repo.tree(sha)) {
      const t = mode === '040000' ? 'tree' : mode === '160000' ? 'commit' : 'blob';
      g.out(`${mode} ${t} ${s}\t${cQuote(n)}\n`);
    }
  } else g.out(decodeBytes(data));
  return 0;
}

function gitRevList(g: Git, args: string[]): number {
  args = args.map(a => /^-\d+$/.test(a) ? `-n${a.slice(1)}` : a);
  const o = parse('rev-list', args);
  const repo = g.repo();
  let [include, exclude, paths] = revisionsAndPaths(repo, o);
  if (o.has('\x02')) {
    include = [...include, ...allRefCommits(repo)];
    if (repo.head()) include.push(repo.head()!);
  }
  if (!include.length) throw usageError('rev-list', subMsg('rev-list', 'noRevisions'));
  const excluded = exclude.length ? ancestors(repo, exclude, g.check) : new Set<string>();
  let order = walkOrder(repo, dedupe(include), g.check).filter(c => !excluded.has(c));
  const specs = userSpecs(repo, paths);
  if (specs !== null) order = order.filter(c => diffFiles(treeFiles(repo, repo.commit(c).parents[0] ?? null), repo.flatten(repo.commit(c).tree), specs).length);
  const n = o.val('n');
  if (n !== null && isDigits(n)) order = order.slice(0, Number(n));
  if (o.has('\x03')) order.reverse();
  if (o.has('\x01')) g.out(`${order.length}\n`);
  else g.out(order.map(c => [c, ...(o.has('\x05') ? repo.commit(c).parents : [])].join(' ') + '\n').join(''));
  return 0;
}

function gitClean(g: Git, args: string[]): number {
  const o = parse('clean', args);
  const repo = g.repo();
  if (!(o.has('f') || o.has('n')) && configBool(g.config('clean.requireforce')) !== false) throw subFatal('clean', 'requireForce');
  const specs = userSpecs(repo, [...o.args, ...o.paths]);
  const tracked = new Set(repo.index().map(e => e.path));
  let cands: string[];
  if (o.has('X')) cands = [...repo.walk('')].filter(p => !tracked.has(p) && repo.ignored(rstripChars(p, '/'), p.endsWith('/')));
  else if (o.has('x')) {
    const saved = repo.ignored;
    repo.ignored = () => false;
    try { cands = untracked(repo, tracked, 'normal'); } finally { repo.ignored = saved; }
  } else cands = untracked(repo, tracked, 'normal');
  for (const p of cands) {
    if (!pathspecMatch(specs, rstripChars(p, '/'))) continue;
    if (p.endsWith('/') && !o.has('d')) continue; // without -d only the untracked files directly in tracked directories go
    if (p.endsWith('/') && repo.fs.exists(repo.wt(p) + '.git', false)) continue;
    if (o.has('n')) { g.out(subMsg('clean', 'wouldRemove', { path: repo.show(p) }) + '\n'); continue; }
    if (!o.has('q')) g.out(subMsg('clean', 'removing', { path: repo.show(p) }) + '\n');
    repo.fs.delete(repo.wt(rstripChars(p, '/')), true);
  }
  return 0;
}

// ---------- config ----------

function gitConfig(g: Git, args: string[]): number {
  const o = parse('config', args);
  const M = (k: string, v: Values = {}) => subMsg('config', k, v);
  const scope = o.has('\x01') ? 'global' : o.has('\x02') ? 'local' : null;
  if (o.has('l')) {
    if (scope === 'global' || scope === null) for (const [n, v] of g.globalConfig()[1].items()) g.out(`${n}=${v}\n`);
    if (scope !== 'global') {
      const r = g.findRepo();
      if (r === null && scope === 'local') throw subFatal('config', 'notInGitDir');
      if (r !== null) for (const [n, v] of r.config().items()) g.out(`${n}=${v}\n`);
    }
    if (scope === null) for (const [n, v] of g.overrides) g.out(`${n}=${v}\n`);
    return 0;
  }
  if (o.has('\x03')) { // --unset
    if (o.args.length !== 1) throw usageError('config', M('wrongArgs'));
    return configWrite(g, scope, canonicalKey(o.args[0]), null);
  }
  const get = o.has('\x04');
  if (!o.args.length || o.args.length > 2) throw usageError('config', M('wrongArgs'));
  const name = o.args[0];
  if (!validKey(name)) { g.err(M(!name.includes('.') ? 'noSection' : 'invalidKey', { name }) + '\n'); return 1; }
  const key = canonicalKey(name);
  if (o.args.length === 1 || get) {
    let v: string | null;
    if (scope === 'global') v = g.globalConfig()[1].get(key);
    else if (scope === 'local') {
      const r = g.findRepo();
      if (r === null) throw subFatal('config', 'notInGitDir');
      v = r.config().get(key);
    } else v = g.config(key);
    if (v === null) return 1;
    if (o.has('\x05')) {
      const b = configBool(v);
      if (b === null) throw subFatal('config', 'badBool', { value: v, name: key });
      v = b ? 'true' : 'false';
    }
    g.out(v + '\n');
    return 0;
  }
  return configWrite(g, scope, key, o.args[1]);
}

function configWrite(g: Git, scope: string | null, key: string, val: string | null): number {
  if (scope === 'global') {
    const [path, cfg] = g.globalConfig();
    const changed = cfg.set(key, val);
    if (val === null && !changed) return 5;
    g.ctx.fs.writeBytes(path, encodeText(cfg.text()));
    return 0;
  }
  const r = g.findRepo();
  if (r === null) throw subFatal('config', 'notInGitDir');
  const changed = r.config().set(key, val);
  if (val === null && !changed) return 5;
  r.saveConfig();
  return 0;
}

function gitRemote(g: Git, args: string[]): number {
  const o = parse('remote', args);
  const repo = g.repo();
  if (o.args.length) throw subFatal('remote', 'noRemotes');
  const remotes = new Map<string, Map<string, string>>(); // the remotes a copied clone names, in config order
  for (const [key, value] of repo.config().items()) {
    if (!key.startsWith('remote.') || key.split('.').length < 3) continue;
    const rest = key.slice(7), dot = rest.lastIndexOf('.');
    const name = rest.slice(0, dot), field = rest.slice(dot + 1);
    if (!remotes.has(name)) remotes.set(name, new Map());
    const f = remotes.get(name)!;
    if (!f.has(field)) f.set(field, value);
  }
  for (const [name, f] of remotes) {
    if (o.has('v')) g.out(`${name}\t${f.get('url') ?? ''} (fetch)\n${name}\t${f.get('pushurl') ?? f.get('url') ?? ''} (push)\n`);
    else g.out(name + '\n');
  }
  return 0;
}

function gitMergeBase(g: Git, args: string[]): number {
  const o = parse('merge-base', args);
  const repo = g.repo();
  if (o.args.length < (o.has('\x01') ? 1 : 2) && !(o.has('\x02') && o.args.length))
    throw new GitError(rstripChars(subSpec('merge-base').help, '\n'), 129);
  if (o.has('\x02') && o.args.length !== 2) throw subFatal('merge-base', 'isAncestorArgs');
  const commits = o.args.map(a => {
    const sha = tryResolve(repo, a);
    const c = sha ? peel(repo, sha, 'commit') : null;
    if (c === null) throw subFatal('merge-base', 'notValid', { name: a });
    return c;
  });
  const check = g.check;
  if (o.has('\x02')) return ancestors(repo, [commits[1]], check).has(commits[0]) ? 0 : 1;
  let common: Set<string>;
  if (o.has('\x01')) { // common to all of them
    common = ancestors(repo, [commits[0]], check);
    for (const c of commits.slice(1)) { const s = ancestors(repo, [c], check); common = new Set([...common].filter(x => s.has(x))); }
  } else { // the first against a merge of the others
    const others = ancestors(repo, commits.slice(1), check);
    common = new Set([...ancestors(repo, [commits[0]], check)].filter(x => others.has(x)));
  }
  if (!common.size) return 1;
  const below = ancestors(repo, [...common].flatMap(c => repo.commit(c).parents), check);
  const order = walkOrder(repo, [...common].filter(c => !below.has(c)).sort(), check);
  g.out((o.has('a') ? order : order.slice(0, 1)).map(c => c + '\n').join(''));
  return 0;
}

function gitFsck(g: Git, args: string[]): number {
  const o = parse('fsck', args);
  const repo = g.repo();
  const check = g.check;
  const present = new Map<string, string>(); // every stored object -> its path (loose) or pack
  let code = 0;
  const names: [string, string][] = [];
  const base = repo.gpath('objects');
  for (const d of repo.fs.isDirectory(base, false) ? repo.fs.list(base).sort() : []) {
    if (/^[0-9a-f]{2}$/.test(d) && repo.fs.isDirectory(`${base}/${d}`, false))
      for (const n of repo.fs.list(`${base}/${d}`).sort()) if (/^[0-9a-f]{38}$/.test(n)) names.push([d + n, `.git/objects/${d}/${n}`]);
  }
  for (const pack of repo.packs()) for (const sha of pack.offsets.keys()) names.push([sha, pack.path]);
  if (names.length > MAX_WALK) throw fatal('tooLarge', { what: 'objects' });
  const kinds = new Map<string, string>();
  const links = new Map<string, [string, string][]>(); // object -> [kind, id][] it points to
  for (const [sha, path] of names) {
    check();
    if (present.has(sha)) continue;
    present.set(sha, path);
    try {
      const [kind, data] = repo.obj(sha);
      kinds.set(sha, kind);
      if (kind === 'commit') {
        const c = repo.commit(sha);
        links.set(sha, [['tree', c.tree], ...c.parents.map((p): [string, string] => ['commit', p])]);
      } else if (kind === 'tree') {
        links.set(sha, parseTree(data, sha).filter(([m]) => m !== '160000').map(([m, , x]): [string, string] => [m === '040000' ? 'tree' : 'blob', x]));
      } else if (kind === 'tag') {
        const m = /^object ([0-9a-f]{40})\ntype (\w+)/m.exec(bytesToText(data));
        links.set(sha, m ? [[m[2], m[1]]] : []);
      }
    } catch (e) {
      if (!(e instanceof GitError)) throw e;
      g.err(subMsg('fsck', 'corrupt', { object: sha, path }) + '\n');
      code |= 1;
    }
  }
  const roots: [string, string][] = [];
  for (const e of repo.index()) if (e.mode !== '160000') roots.push(['blob', e.sha]);
  const head = repo.head();
  if (head) roots.push(['commit', head]);
  for (const [, s] of repo.refs('refs/')) roots.push(['commit', s]);
  const logs = repo.gpath('logs');
  const stack = repo.fs.isDirectory(logs, false) ? [logs] : [];
  while (stack.length) {
    const d = stack.pop()!;
    for (const n of sortCp(repo.fs.list(d))) {
      const full = d + '/' + n;
      if (repo.fs.isDirectory(full, false)) stack.push(full);
      else if (repo.fs.isFile(full, false)) {
        for (const line of decodeBytes(repo.fs.readBytes(full)).split('\n'))
          for (const x of line.split(' ').slice(0, 2)) if (/^[0-9a-f]{40}$/.test(x) && x !== '0'.repeat(40)) roots.push(['commit', x]);
      }
    }
  }
  const reachable = new Set<string>();
  const missing = new Map<string, string>();
  const stack2: [string, string, string | null][] = roots.map(([k, x]): [string, string, string | null] => [k, x, null]).reverse();
  while (stack2.length) {
    const [kind, sha, parent] = stack2.pop()!;
    if (reachable.has(sha)) continue;
    reachable.add(sha);
    if (reachable.size % 256 === 0) check();
    if (!kinds.has(sha)) {
      if (!present.has(sha)) {
        if (parent !== null)
          g.out(subMsg('fsck', 'brokenLink', { fromKind: rjust(kinds.get(parent)!, 7), from: parent, toKind: rjust(kind, 7), to: sha }) + '\n');
        missing.set(sha, kind);
      }
      continue;
    }
    stack2.push(...(links.get(sha) ?? []).map(([k, x]): [string, string, string | null] => [k, x, sha]).reverse());
  }
  if (missing.size) code |= 2;
  const referenced = new Set([...links.values()].flatMap(ls => ls.map(([, x]) => x)));
  const report: [string, string][] = [...missing].map(([sha, k]) => [sha, subMsg('fsck', 'missing', { kind: k, object: sha })]);
  if (!o.has('!\x01'))
    for (const [sha, k] of kinds) if (!reachable.has(sha) && !referenced.has(sha)) report.push([sha, subMsg('fsck', 'dangling', { kind: k, object: sha })]);
  // shortcut: git prints these in its hash table's order; sorted by id here
  report.sort((x, y) => x[0] < y[0] ? -1 : x[0] > y[0] ? 1 : x[1] < y[1] ? -1 : x[1] > y[1] ? 1 : 0);
  g.out(report.map(([, line]) => line + '\n').join(''));
  return code;
}

// ---------- patches: apply, am, format-patch ----------

type Hunk = [oldStart: number, oldCount: number, newStart: number, newCount: number, lines: [string, string][]];

/** One file's part of a unified diff. old/new are paths (null: /dev/null); hunks are
 * [old_start, old_count, new_start, new_count, [sign, line][]]. */
class FilePatch {
  old: string | null = null;
  neu: string | null = null;
  oldMode: string | null = null;
  newMode: string | null = null;
  similarity = 100;
  renamed = false;
  binary = false;
  hunks: Hunk[] = [];

  counts(): [number, number] {
    let a = 0, d = 0;
    for (const h of this.hunks) for (const [s] of h[4]) { if (s === '+') a++; else if (s === '-') d++; }
    return [a, d];
  }

  reverse(): void {
    [this.old, this.neu, this.oldMode, this.newMode] = [this.neu, this.old, this.newMode, this.oldMode];
    const flip: Record<string, string> = { '+': '-', '-': '+' };
    this.hunks = this.hunks.map(([os, oc, ns, nc, lines]): Hunk => [ns, nc, os, oc, lines.map(([s, t]): [string, string] => [flip[s] ?? s, t])]);
  }
}

/** A name as git quotes it ("a\tb", octal escapes for other bytes) back to the name. */
function cUnquote(s: string): string {
  if (!(s.length >= 2 && s[0] === '"' && s[s.length - 1] === '"')) return s;
  const out: number[] = [];
  const body = s.slice(1, -1);
  const ESC: Record<string, number> = { a: 7, b: 8, t: 9, n: 10, v: 11, f: 12, r: 13 };
  let i = 0;
  while (i < body.length) {
    const c = body[i];
    if (c === '\\' && i + 1 < body.length) {
      const n = body[i + 1];
      if (/^[0-7]{3}$/.test(body.slice(i + 1, i + 4))) { out.push(parseInt(body.slice(i + 1, i + 4), 8) & 0xff); i += 4; continue; }
      if (n in ESC) out.push(ESC[n]); else out.push(...encodeText(n));
      i += 2;
      continue;
    }
    const cp = String.fromCodePoint(body.codePointAt(i)!);
    out.push(...encodeText(cp));
    i += cp.length;
  }
  return decodeBytes(new Uint8Array(out));
}

/** The file patches in a unified diff (git's extended headers or plain diff -u). Text around them, such as a
 * mail's message or signature, is skipped. A hunk cut short or with a stray line is a corrupt patch. */
function parsePatch(data: Uint8Array, strip: number): FilePatch[] {
  const lines = splitLines(data);
  const n = lines.length;
  const text = (k: number) => rstripChars(lines[k], '\n');
  const name = (raw: string, cut: number): string | null => {
    raw = raw.startsWith('"') ? raw : raw.split('\t')[0];
    raw = cUnquote(rstripChars(raw, ' \t\r\n\v\f'));
    if (raw === '/dev/null') return null;
    const parts = raw.split('/');
    return parts.length > cut ? parts.slice(cut).join('/') : raw;
  };
  const corrupt = (k: number) => subFatal('apply', 'corrupt', { line: String(k + 1) });
  const starts = (s: string, ...ps: string[]) => ps.some(p => s.startsWith(p));
  const out: FilePatch[] = [];
  let i = 0;
  while (i < n) {
    const t = text(i);
    let fp: FilePatch;
    if (t.startsWith('diff --git ')) {
      fp = new FilePatch();
      const rest = t.slice(11);
      const m = /^("(?:[^"\\]|\\.)*"|\S+) ("(?:[^"\\]|\\.)*"|\S+)$/.exec(rest) ?? /^(a\/.*) (b\/.*)$/.exec(rest);
      if (m) [fp.old, fp.neu] = [name(m[1], strip), name(m[2], strip)];
      i++;
      while (i < n && !starts(text(i), 'diff --git ', '--- ', '@@ ')) {
        const h = text(i);
        if (h.startsWith('new file mode ')) [fp.old, fp.newMode] = [null, h.slice(14)];
        else if (h.startsWith('deleted file mode ')) [fp.neu, fp.oldMode] = [null, h.slice(18)];
        else if (h.startsWith('old mode ')) fp.oldMode = h.slice(9);
        else if (h.startsWith('new mode ')) fp.newMode = h.slice(9);
        else if (starts(h, 'rename from ', 'copy from ')) [fp.old, fp.renamed] = [cUnquote(h.split(' ').slice(2).join(' ')), h.startsWith('rename')];
        else if (starts(h, 'rename to ', 'copy to ')) fp.neu = cUnquote(h.split(' ').slice(2).join(' '));
        else if (h.startsWith('similarity index ')) fp.similarity = Number(rstripChars(h.slice(17), '%') || 0) || 0;
        else if (starts(h, 'Binary files ', 'GIT binary patch')) fp.binary = true;
        else if (!starts(h, 'index ', 'dissimilarity index ')) break;
        i++;
      }
      if (i < n && text(i).startsWith('--- ')) {
        if (i + 1 >= n || !text(i + 1).startsWith('+++ ')) throw corrupt(i + 1);
        const old = name(text(i).slice(4), strip), neu = name(text(i + 1).slice(4), strip);
        fp.old = old === null || !fp.renamed ? old : fp.old;
        fp.neu = neu === null || !fp.renamed ? neu : fp.neu;
        i += 2;
      }
    } else if (t.startsWith('--- ') && i + 1 < n && text(i + 1).startsWith('+++ ')) {
      fp = new FilePatch();
      const old = name(t.slice(4), strip), neu = name(text(i + 1).slice(4), strip);
      fp.old = old === null ? null : (neu || old); // plain diffs patch the +++ file
      fp.neu = neu;
      i += 2;
    } else { i++; continue; }
    while (i < n && text(i).startsWith('@@ ')) {
      const m = /^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@/.exec(text(i));
      if (!m) throw corrupt(i);
      const os = Number(m[1]), oc = Number(m[2] ?? 1), ns = Number(m[3]), nc = Number(m[4] ?? 1);
      i++;
      const body: [string, string][] = [];
      let ro = oc, rn = nc;
      while (ro > 0 || rn > 0) {
        if (i >= n) throw corrupt(i);
        const line = lines[i];
        const sign = line === '\n' || line === '\r\n' ? '' : line[0];
        if (sign === '\\' && body.length) {
          body[body.length - 1] = [body[body.length - 1][0], rstripChars(body[body.length - 1][1], '\n')];
          i++;
          continue;
        }
        if (sign === '' || sign === ' ') { ro--; rn--; }
        else if (sign === '-') ro--;
        else if (sign === '+') rn--;
        else throw corrupt(i);
        if (ro < 0 || rn < 0) throw corrupt(i);
        body.push([sign || ' ', sign ? line.slice(1) : line]);
        i++;
      }
      if (i < n && lines[i].startsWith('\\') && body.length) {
        body[body.length - 1] = [body[body.length - 1][0], rstripChars(body[body.length - 1][1], '\n')];
        i++;
      }
      fp.hunks.push([os, oc, ns, nc, body]);
    }
    out.push(fp);
  }
  return out;
}

class ApplyFailed extends Error {}

/** data with fp's hunks applied. Each hunk's lines must match exactly, though it may sit at an offset from the line
 * the patch names (nearest first); there is no fuzz. */
function applyHunks(g: Git, data: Uint8Array, fp: FilePatch, path: string, verbose: boolean): Uint8Array {
  const lines = splitLines(data);
  const out: string[] = [];
  let pos = 0, offset = 0;
  fp.hunks.forEach(([os, oc, , , body], k) => {
    g.ctx.checkTime();
    const pre = body.filter(([s]) => s !== '+').map(([, t]) => t);
    const post = body.filter(([s]) => s !== '-').map(([, t]) => t);
    const start = (oc ? os - 1 : os) + offset;
    const fits = (p: number) => pos <= p && p <= lines.length - pre.length && pre.every((t, j) => lines[p + j] === t);
    let found: number | null = null;
    for (let d = 0; d <= Math.max(lines.length, 1) && found === null; d++) {
      for (const p of d ? [start + d, start - d] : [start]) if (fits(p)) { found = p; break; }
    }
    if (found === null) {
      if (verbose) g.err(subMsg('apply', 'searching', { lines: pre.join('') }) + '\n');
      g.err(subMsg('apply', 'patchFailed', { path, line: String(os) }) + '\n');
      throw new ApplyFailed();
    }
    const moved = found - (oc ? os - 1 : os);
    if (moved && verbose)
      g.err(subMsg('apply', 'hunkOffset', { number: String(k + 1), line: String(found + 1), offset: `${moved} line${Math.abs(moved) !== 1 ? 's' : ''}` }) + '\n');
    out.push(...lines.slice(pos, found), ...post);
    pos = found + pre.length;
    offset = moved;
  });
  return encodeText([...out, ...lines.slice(pos)].join(''));
}

/** Checks every file patch against the work tree and/or the index, then writes them all, or nothing when one fails
 * (printing why). index and worktree: git apply --index; index only: --cached. */
function applyPatches(g: Git, repo: Repo | null, top: string, patches: FilePatch[], index: boolean, worktree: boolean, verbose: boolean,
  check: boolean): boolean {
  const fs = g.ctx.fs;
  const entries = new Map<string, Entry>(repo && index ? repo.index().filter(e => e.stage === 0).map(e => [e.path, e]) : []);
  const state = new Map<string, [string, Uint8Array] | null>(); // path -> [mode, data] after the patches so far; null: removed
  const current = (p: string): [string, Uint8Array] | null => {
    if (state.has(p)) return state.get(p)!;
    if (worktree) {
      const full = rstripChars(top, '/') + '/' + p;
      if (repo) return repo.wtStat(p);
      if (fs.isFile(full)) return [fs.stat(full).mode & 0o100 ? '100755' : '100644', fs.readBytes(full)];
      return null;
    }
    const e = entries.get(p);
    return e ? [e.mode, repo!.typed(e.sha, 'blob')] : null;
  };
  let ok = true;
  for (const fp of patches) {
    const path = fp.neu || fp.old || '';
    for (const p of [fp.old, fp.neu]) if (p !== null) checkPath(p); // both sides: a rename must not reach outside the work tree either
    if (verbose) g.err(subMsg('apply', 'checking', { path }) + '\n');
    if (fp.binary) {
      g.err(subMsg('apply', 'binary', { path }) + '\n');
      ok = false;
      continue;
    }
    let oldMode: string | null, oldData: Uint8Array;
    try {
      if (fp.old === null) {
        if (current(path) !== null || (index && worktree && entries.has(path))) {
          g.err(subMsg('apply', worktree ? 'alreadyExists' : 'alreadyInIndex', { path }) + '\n');
          throw new ApplyFailed();
        }
        [oldMode, oldData] = [null, new Uint8Array(0)];
      } else {
        const have = current(fp.old);
        if (have === null) {
          g.err(subMsg('apply', worktree ? 'noSuchFile' : 'notInIndex', { path: fp.old }) + '\n');
          throw new ApplyFailed();
        }
        if (index && worktree && !state.has(fp.old)) {
          const e = entries.get(fp.old);
          if (!e || objectId('blob', have[1]) !== e.sha) {
            g.err(subMsg('apply', 'notMatchIndex', { path: fp.old }) + '\n');
            throw new ApplyFailed();
          }
        }
        [oldMode, oldData] = have;
      }
    } catch (e) {
      if (!(e instanceof ApplyFailed)) throw e;
      ok = false;
      continue;
    }
    let newData: Uint8Array;
    try {
      newData = applyHunks(g, oldData, fp, fp.old || path, verbose);
    } catch (e) {
      if (!(e instanceof ApplyFailed)) throw e;
      g.err(subMsg('apply', 'doesNotApply', { path: fp.old || path }) + '\n');
      ok = false;
      continue;
    }
    if (fp.old !== null && fp.old !== fp.neu) state.set(fp.old, null);
    if (fp.neu !== null) state.set(fp.neu, [fp.newMode || oldMode || '100644', newData]);
  }
  if (!ok || check) return ok;
  if (verbose) for (const fp of patches) g.err(subMsg('apply', 'appliedCleanly', { path: (fp.neu || fp.old)! }) + '\n');
  for (const [p, v] of state) {
    g.ctx.checkTime();
    if (worktree) {
      const full = rstripChars(top, '/') + '/' + p;
      if (v === null) {
        if (repo) repo.wtRemove(p);
        else if (fs.exists(full, false)) fs.delete(full);
      } else {
        fs.mkdir(VirtualFileSystem.getParent(full), true);
        fs.writeBytes(full, v[1]);
        fs.chmod(full, (v[0] === '100755' ? 0o777 : 0o666) & ~fs.umask);
      }
    }
    if (index) {
      if (v === null) entries.delete(p);
      else {
        const sha = repo!.put('blob', v[1]);
        entries.set(p, worktree ? repo!.entryFor(p, v[0], sha) : new Entry(p, sha, v[0]));
      }
    }
  }
  if (index) repo!.saveIndex(entries.values());
  return true;
}

function applyStat(g: Git, patches: FilePatch[], numstat: boolean, stat: boolean, summary: boolean): void {
  const label = (fp: FilePatch) => fp.old && fp.neu && fp.old !== fp.neu ? `${fp.old} => ${fp.neu}` : cQuote((fp.neu || fp.old)!);
  const plural = (n: number, word: string) => `${n} ${word}${n !== 1 ? 's' : ''}`;
  if (numstat) {
    for (const fp of patches) {
      const [a, d] = fp.counts();
      g.out((fp.binary ? '-\t-\t' : `${a}\t${d}\t`) + label(fp) + '\n');
    }
  }
  if (stat) {
    const names = patches.map(label);
    const width = Math.min(Math.max(0, ...names.map(cpLen)), 50);
    const room = 70 - width;
    const topChange = Math.max(0, ...patches.map(fp => fp.counts()[0] + fp.counts()[1]));
    patches.forEach((fp, k) => {
      let [a, d] = fp.counts();
      const total = a + d;
      if (topChange > room) [a, d] = [Math.ceil(a * room / topChange), Math.ceil(d * room / topChange)]; // rounded up: a change never vanishes
      g.out(` ${ljust(names[k], width)} |${String(total).padStart(5)} ${'+'.repeat(a)}${'-'.repeat(d)}\n`);
    });
    const ins = patches.reduce((n, fp) => n + fp.counts()[0], 0);
    const dels = patches.reduce((n, fp) => n + fp.counts()[1], 0);
    g.out(` ${plural(patches.length, 'file')} changed, ${plural(ins, 'insertion')}(+), ${plural(dels, 'deletion')}(-)\n`);
  }
  if (summary) {
    for (const fp of patches) {
      if (fp.old === null) g.out(` create mode ${fp.newMode || '100644'} ${cQuote(fp.neu!)}\n`);
      else if (fp.neu === null) g.out(` delete mode ${fp.oldMode || '100644'} ${cQuote(fp.old)}\n`);
      else {
        if (fp.old !== fp.neu) g.out(` rename ${renameLabel(fp.old, fp.neu)} (${fp.similarity}%)\n`);
        if (fp.oldMode && fp.newMode && fp.oldMode !== fp.newMode) g.out(` mode change ${fp.oldMode} => ${fp.newMode} ${cQuote(fp.neu)}\n`);
      }
    }
  }
}

function gitApply(g: Git, args: string[]): number {
  const o = parse('apply', args);
  let repo = g.findRepo();
  if (repo === null && (o.has('\x05') || o.has('\x06'))) repo = g.repo(); // the index needs a repository: fails with git's message
  const top = repo ? repo.top : g.cwd;
  const strip = isDigits(o.val('p') ?? '') ? Number(o.val('p')) : 1;
  let patches: FilePatch[] = [];
  for (const f of o.args.length ? o.args : ['-']) {
    let data: Uint8Array;
    if (f === '-') data = encodeText(g.ctx.stdinText);
    else {
      const path = normalize(g.cwd, f);
      if (!g.ctx.fs.isFile(path))
        throw new GitError(subMsg('apply', 'cannotOpen', { file: f, errno: g.ctx.fs.isDirectory(path) ? 'Is a directory' : 'No such file or directory' }), 128);
      data = g.ctx.fs.readBytes(path);
    }
    patches.push(...parsePatch(data, strip));
  }
  if (!patches.length) throw subFatal('apply', 'noPatches');
  if (repo && g.cwd !== repo.top) {
    // from a subdirectory, files outside it are left alone, as git does
    const here = g.cwd.slice(rstripChars(repo.top, '/').length + 1) + '/';
    patches = patches.filter(fp => (fp.neu || fp.old || '').startsWith(here));
  }
  if (o.has('R')) for (const fp of patches) fp.reverse();
  const informative = o.has('\x01') || o.has('\x02') || o.has('\x03');
  applyStat(g, patches, o.has('\x02'), o.has('\x01'), o.has('\x03'));
  if (informative && !o.has('\x07')) return 0; // --stat and friends only describe the patch, even with --check
  const cached = o.has('\x06');
  const ok = applyPatches(g, repo, top, patches, cached || o.has('\x05'), !cached, o.has('v'), o.has('\x04'));
  return ok ? 0 : 1;
}

// ---------- am ----------

type Mail = [name: string, email: string, date: string | null, subject: string, body: string, patch: Uint8Array];

/** [name, email, date, subject, body, patch] from one message as git format-patch writes it. */
function parseMail(text: string): Mail {
  const [head, , rest] = partition(text, '\n\n');
  const headers = new Map<string, string>();
  let last: string | null = null;
  for (const line of head.split('\n')) {
    if ((line[0] === ' ' || line[0] === '\t') && last) headers.set(last, headers.get(last) + ' ' + line.trim());
    else if (line.includes(':') && !line.startsWith('From ')) {
      const [k, , v] = partition(line, ':');
      last = k.trim().toLowerCase();
      headers.set(last, v.trim());
    }
  }
  const from = headers.get('from') ?? '';
  const m = /^\s*(.*?)\s*<([^<>]*)>\s*$/.exec(from);
  const [name, email] = m ? [stripChars(m[1], '"'), m[2]] : [from, from];
  let subject = headers.get('subject') ?? '';
  subject = subject.replace(/^(\s*(\[[^\]]*\]|re:)\s*)+/i, '');
  subject = subject.replace(/\s+/g, ' ').trim();
  const bodyLines: string[] = [];
  let patchAt: number | null = null;
  const lines = rest.split('\n');
  for (let k = 0; k < lines.length; k++) {
    const line = lines[k];
    if (line === '---' || line.startsWith('diff --git ') || line.startsWith('Index: ') || (line.startsWith('--- ') && k + 1 < lines.length && lines[k + 1].startsWith('+++ '))) {
      patchAt = k;
      break;
    }
    bodyLines.push(line);
  }
  const body = stripChars(bodyLines.join('\n'), '\n');
  const patch = patchAt !== null ? lines.slice(patchAt).join('\n') : '';
  return [name, email, headers.get('date') ?? null, subject, body, encodeText(patch)];
}

/** The messages in a mailbox: each starts at a line beginning 'From '; a file without one is a single message. */
function splitMbox(text: string): string[] {
  if (!text.startsWith('From ')) return /^(From|Subject):/m.test(text) ? [text] : [];
  const out: string[] = [];
  let cur: string[] | null = null;
  for (const line of text.split('\n')) {
    if (line.startsWith('From ') && /^From \S+ \w{3} \w{3} [ \d]\d \d\d:\d\d:\d\d \d{4}/.test(line)) {
      if (cur !== null) out.push(cur.join('\n'));
      cur = [];
      continue;
    }
    if (cur !== null) cur.push(line);
  }
  if (cur !== null) out.push(cur.join('\n'));
  return out;
}

function gitAm(g: Git, args: string[]): number {
  let show: string | null = null;
  const rest: string[] = [];
  for (const a of args) {
    if (/^--show-current-patch(=.*)?$/s.test(a)) show = partition(a, '=')[2] || 'raw';
    else rest.push(a);
  }
  const o = parse('am', rest);
  const repo = g.repo();
  const st = 'rebase-apply';
  const busy = repo.gexists(`${st}/next`);
  const pad = (k: number) => String(k).padStart(4, '0');
  const mail = (k: number) => decodeBytes(repo.gread(`${st}/${pad(k)}`) ?? new Uint8Array(0));
  const number = () => Number((repo.gtext(`${st}/next`) ?? '1').trim() || 1);
  const finish = () => repo.fs.delete(repo.gpath(st), true);
  if (o.has('\x03') || o.has('\x02') || o.has('\x01') || show) {
    if (!busy) throw subFatal('am', 'notInProgress');
    if (show) {
      const text = mail(number());
      g.out(show === 'diff' ? decodeBytes(parseMail(text)[5]) : text);
      return 0;
    }
    const head = repo.head();
    if (o.has('\x03')) {
      const orig = (repo.gtext('ORIG_HEAD') ?? '').trim() || head;
      if ((repo.gtext(`${st}/abort-safety`) ?? '').trim() !== (head ?? '')) {
        g.err(subMsg('am', 'movedHead') + '\n'); // HEAD moved since the failure: only the session is dropped
      } else {
        switchTrees(g, repo, head, orig, true);
        if (orig && orig !== head) repo.updateRef('HEAD', orig, 'am --abort');
      }
      finish();
      return 0;
    }
    if (o.has('\x02')) {
      switchTrees(g, repo, head, head, true);
      repo.gwrite(`${st}/next`, `${number() + 1}\n`);
    } else {
      const [name, email, date, subject, body] = parseMail(mail(number()));
      const tree = repo.writeTree(indexFiles(repo.index()));
      if (tree === (head ? repo.commit(head).tree : EMPTY_TREE)) {
        g.out(subMsg('am', 'applying', { subject }) + '\n');
        g.out(subMsg('am', 'noChanges') + '\n' + subMsg('am', 'resolveHint') + '\n');
        return 128;
      }
      if (!o.has('q')) g.out(subMsg('am', 'applying', { subject }) + '\n');
      amCommit(g, repo, tree, name, email, date, subject, body);
      repo.gwrite(`${st}/next`, `${number() + 1}\n`);
    }
  } else {
    if (busy) throw subFatal('am', 'inProgress');
    const texts: string[] = [];
    for (const f of o.args.length ? o.args : ['-']) {
      if (f === '-') { texts.push(g.ctx.stdinText); continue; }
      const path = normalize(g.cwd, f);
      if (!g.ctx.fs.isFile(path)) throw subFatal('am', 'cannotOpen', { file: f, errno: 'No such file or directory' });
      texts.push(decodeBytes(g.ctx.fs.readBytes(path)));
    }
    const mails = texts.flatMap(splitMbox);
    if (!mails.length) throw new GitError(subMsg('am', 'notPatch'), 128);
    const head = repo.head();
    mails.forEach((m, k) => repo.gwrite(`${st}/${pad(k + 1)}`, m));
    repo.gwrite(`${st}/last`, `${mails.length}\n`);
    repo.gwrite(`${st}/next`, '1\n');
    repo.gwrite(`${st}/abort-safety`, (head ?? '') + '\n');
    if (head) repo.gwrite('ORIG_HEAD', head + '\n');
    const staged = diffFiles(treeFiles(repo, head), indexFiles(repo.index()));
    if (staged.length) // like git, the session is already set up and stays behind
      throw subFatal('am', 'dirtyIndex', { paths: staged.map(([, old, neu]) => (neu || old)!).join(', ') });
  }
  const last = Number((repo.gtext(`${st}/last`) ?? '0').trim() || 0);
  while (number() <= last) {
    const k = number();
    const [name, email, date, subject, body, patch] = parseMail(mail(k));
    if (!o.has('q')) g.out(subMsg('am', 'applying', { subject }) + '\n');
    let patches: FilePatch[] | null;
    try {
      patches = parsePatch(patch, 1);
    } catch (e) {
      if (!(e instanceof GitError)) throw e;
      g.err(e.message + '\n');
      patches = null;
    }
    if (patches !== null && !patches.length) {
      repo.gwrite(`${st}/abort-safety`, (repo.head() ?? '') + '\n');
      g.out(subMsg('am', 'emptyPatch') + '\n' + subMsg('am', 'resolveHint') + '\n');
      return 128;
    }
    if (patches === null || !applyPatches(g, repo, repo.top, patches, true, true, false, false)) {
      g.err(subMsg('am', 'showHint') + '\n');
      repo.gwrite(`${st}/abort-safety`, (repo.head() ?? '') + '\n');
      g.out(subMsg('am', 'patchFailedAt', { number: pad(k), subject }) + '\n' + subMsg('am', 'resolveHint') + '\n');
      return 128;
    }
    const tree = repo.writeTree(indexFiles(repo.index()));
    amCommit(g, repo, tree, name, email, date, subject, body);
    repo.gwrite(`${st}/next`, `${k + 1}\n`);
  }
  finish();
  return 0;
}

function amCommit(g: Git, repo: Repo, tree: string, name: string, email: string, date: string | null, subject: string, body: string): void {
  const head = repo.head();
  const author = g.ident('author', true, name, email, date);
  const committer = g.ident('committer');
  const message = subject + (body ? '\n\n' + body : '') + '\n';
  const data = `tree ${tree}\n` + (head ? `parent ${head}\n` : '') + `author ${author}\ncommitter ${committer}\n\n${message}`;
  const sha = repo.put('commit', data);
  repo.updateRef('HEAD', sha, `am: ${subject}`);
}

// ---------- format-patch ----------

function gitFormatPatch(g: Git, args: string[]): number {
  args = args.map(a => /^-\d+$/.test(a) ? `-n${a.slice(1)}` : a);
  const o = parse('format-patch', args);
  const repo = g.repo();
  const limit = isDigits(o.val('n') ?? '') ? Number(o.val('n')) : null;
  let include: string[] = [];
  const exclude: string[] = [];
  for (const a of o.args) {
    if (a.includes('..')) {
      const [left, , right] = partition(a, '..');
      exclude.push(commitOf(repo, left || 'HEAD'));
      include.push(commitOf(repo, right || 'HEAD'));
    } else if (limit !== null) include.push(commitOf(repo, a));
    else exclude.push(commitOf(repo, a)); // <since>: the commits after it, up to HEAD
  }
  if (!include.length) {
    if (repo.head() === null) throw fatal('badDefaultRevision');
    include = [repo.head()!];
  }
  const excluded = exclude.length ? ancestors(repo, exclude, g.check) : new Set<string>();
  let order = walkOrder(repo, include, g.check).filter(c => !excluded.has(c) && repo.commit(c).parents.length < 2);
  if (limit !== null) order = order.slice(0, limit);
  order.reverse();
  const total = order.length;
  const outDir = o.val('o');
  const statOpts = new Opts(), patchOpts = new Opts();
  statOpts.flags.set('\x01', 1);
  statOpts.flags.set('\x1f', 1);
  order.forEach((sha, i) => {
    const k = i + 1;
    g.ctx.checkTime();
    const c = repo.commit(sha);
    const [an, ae, at, az] = parseIdent(c.author);
    const lines = rstripChars(c.message, '\n').split('\n');
    let subjectEnd = lines.findIndex(l => !l.trim());
    if (subjectEnd < 0) subjectEnd = lines.length;
    const subject = lines.slice(0, subjectEnd).map(l => l.trim()).join(' ');
    const body = stripChars(lines.slice(subjectEnd).join('\n'), '\n');
    const a = treeFiles(repo, c.parents.length ? c.parents[0] : null), b = repo.flatten(c.tree);
    const stat: string[] = [], patch: string[] = [];
    writeDiff(g, repo, a, b, statOpts, null, false, false, s => { stat.push(s); }); // shortcut: the stat is drawn 80 columns wide, real git uses 72
    writeDiff(g, repo, a, b, patchOpts, null, false, false, s => { patch.push(s); });
    const prefix = total > 1 ? `[PATCH ${k}/${total}]` : '[PATCH]';
    const text = `From ${sha} Mon Sep 17 00:00:00 2001\nFrom: ${an} <${ae}>\nDate: ${formatDate(at, az, 'rfc')}\n`
      + `Subject: ${prefix} ${subject}\n\n` + (body ? body + '\n' : '') + '---\n' + stat.join('') + '\n'
      + patch.join('') + '-- \n' + SPEC().version.split(' ').pop() + '\n\n';
    if (o.has('\x01')) { g.out(text); return; }
    const slug = rstripChars(stripChars(subject.replace(/[^A-Za-z0-9._]+/g, '-'), '-.').slice(0, 52), '-.');
    const name = `${String(k).padStart(4, '0')}-${slug}.patch`;
    const shown = outDir ? rstripChars(outDir, '/') + '/' + name : name;
    const target = normalize(g.cwd, shown);
    g.ctx.fs.mkdir(VirtualFileSystem.getParent(target), true);
    g.ctx.fs.writeBytes(target, encodeText(text));
    g.out(shown + '\n');
  });
  return 0;
}

// ---------- bisect ----------

const bisectMarks = (repo: Repo, kind: string) => repo.refs('refs/bisect/').filter(([n]) => n.startsWith(`refs/bisect/${kind}-`)).map(([, s]) => s);

function bisectLog(repo: Repo, text: string): void {
  repo.gwrite('BISECT_LOG', concat([repo.gread('BISECT_LOG') ?? new Uint8Array(0), encodeText(text)]));
}

function bisectMark(repo: Repo, kind: string, sha: string): void {
  const c = repo.commit(sha);
  repo.updateRef(kind === 'bad' ? 'refs/bisect/bad' : `refs/bisect/${kind}-${sha}`, sha);
  bisectLog(repo, `# ${kind}: [${sha}] ${c.subject}\ngit bisect ${kind} ${sha}\n`);
}

/** About how many more steps n candidates take: log2, rounded down when n is close to a power of two. */
function bisectSteps(n: number): number {
  if (n < 3) return 0;
  const p = 31 - Math.clz32(n);
  return (1 << p) < 3 * (n - (1 << p)) ? p : p - 1;
}

/** Checks out the next commit to test, or reports the first bad one. Returns 'wait', 'next', 'found' or 'skipped'. */
function bisectNext(g: Git, repo: Repo): string {
  const bad = repo.readRef('refs/bisect/bad');
  const goods = bisectMarks(repo, 'good');
  if (!bad || !goods.length) {
    const line = !bad && !goods.length ? subMsg('bisect', 'statusBoth') : bad ? subMsg('bisect', 'statusGood')
      : subMsg('bisect', goods.length === 1 ? 'statusBad' : 'statusBadMany', { count: String(goods.length) });
    g.out(line + '\n');
    bisectLog(repo, `# ${line}\n`);
    return 'wait';
  }
  const check = g.check;
  const good = ancestors(repo, goods, check);
  const candidates = new Set([...ancestors(repo, [bad], check)].filter(c => !good.has(c)));
  if (!candidates.size) throw new GitError(subMsg('bisect', 'bothGoodAndBad', { commit: bad }), 1);
  const skipped = new Set(bisectMarks(repo, 'skip'));
  const order = walkOrder(repo, [bad], check, candidates);
  order.reverse(); // oldest first: of equally good splits, the older one is tested
  const total = candidates.size;
  let best: string | null = null, bestScore = -1, reach = 0;
  if (total > 1) {
    // shortcut: counts reachable candidates per commit (quadratic); fine for the histories a machine holds
    for (const c of order) {
      if (skipped.has(c) || c === bad) continue;
      let w = 0;
      for (const x of ancestors(repo, [c], check)) if (candidates.has(x)) w++;
      if (Math.min(w, total - w) > bestScore) [best, bestScore, reach] = [c, Math.min(w, total - w), w];
    }
  }
  if (best === null) {
    if (total > 1 || skipped.has(bad)) {
      const left = [...[...order].reverse().filter(c => skipped.has(c) && c !== bad), bad];
      g.out(subMsg('bisect', 'onlySkipped', { commits: left.join('\n') }) + '\n');
      return 'skipped';
    }
    const c = repo.commit(bad);
    g.out(subMsg('bisect', 'firstBad', { commit: bad }) + '\n');
    gitShow(g, ['--stat', bad]);
    bisectLog(repo, subMsg('bisect', 'logFirstBad', { commit: bad, subject: c.subject }) + '\n');
    return 'found';
  }
  const left = total - reach - 1, steps = bisectSteps(total);
  moveHead(g, repo, null, best, best, false, true);
  g.out(subMsg('bisect', 'bisecting', { revisions: `${left} revision${left !== 1 ? 's' : ''}`, steps: `${steps} step${steps !== 1 ? 's' : ''}` }) + '\n');
  g.out(subMsg('bisect', 'current', { commit: best, subject: repo.commit(best).subject }) + '\n');
  return 'next';
}

function bisectClear(repo: Repo): void {
  for (const [name] of repo.refs('refs/bisect/')) repo.deleteRef(name);
  for (const f of ['BISECT_START', 'BISECT_LOG']) repo.gremove(f);
}

async function gitBisect(g: Git, args: string[]): Promise<number> {
  const repo = g.repo();
  if (!args.length) throw new GitError(subMsg('bisect', 'needCommand') + '\n\n' + rstripChars(subSpec('bisect').help, '\n'), 129);
  const [action, ...rest] = args;
  if (action.startsWith('-')) parse('bisect', [action]); // an option where the action goes: git's unknown-option usage error
  const busy = repo.gexists('BISECT_START');
  const commitArg = (rev: string): string => {
    const sha = tryResolve(repo, rev);
    const c = sha ? peel(repo, sha, 'commit') : null;
    if (c === null) throw new GitError(subMsg('bisect', 'badRev', { rev }), 1);
    return c;
  };
  if (action === 'start') {
    const named = rest.filter(r => r !== '--');
    const revs = named.map(commitArg);
    if (busy) bisectClear(repo);
    repo.gwrite('BISECT_START', (repo.branch() || repo.head() || '') + '\n');
    bisectLog(repo, 'git bisect start' + named.map(r => ` '${r}'`).join('') + '\n');
    if (revs.length) bisectMark(repo, 'bad', revs[0]);
    for (const r of revs.slice(1)) bisectMark(repo, 'good', r);
    bisectNext(g, repo);
    return 0;
  }
  if (['good', 'bad', 'old', 'new', 'skip'].includes(action)) {
    if (!busy) { g.err(subMsg('bisect', 'needStart') + '\n'); return 1; }
    const kind = action === 'old' ? 'good' : action === 'new' ? 'bad' : action;
    const shas = rest.map(commitArg);
    for (const sha of shas.length ? shas : [commitArg('HEAD')]) bisectMark(repo, kind, sha);
    return bisectNext(g, repo) === 'skipped' ? 2 : 0;
  }
  if (action === 'reset') {
    if (!busy) { g.out(subMsg('bisect', 'notBisecting') + '\n'); return 0; }
    const start = (repo.gtext('BISECT_START') ?? '').trim();
    const target = rest.length ? rest[0] : start;
    const branchSha = repo.readRef('refs/heads/' + target);
    if (branchSha !== null) moveHead(g, repo, 'refs/heads/' + target, branchSha, target, false, false);
    else if (target) moveHead(g, repo, null, commitArg(target), target, false, false);
    bisectClear(repo);
    return 0;
  }
  if (action === 'log') {
    if (!busy) throw new GitError(subMsg('bisect', 'logNotBisecting'), 1);
    g.out(decodeBytes(repo.gread('BISECT_LOG') ?? new Uint8Array(0)));
    return 0;
  }
  if (action === 'run') {
    if (!busy) { g.err(subMsg('bisect', 'needStart') + '\n'); return 1; }
    if (!rest.length) throw new GitError(subMsg('bisect', 'needCommand'), 1);
    if (!repo.readRef('refs/bisect/bad') || !bisectMarks(repo, 'good').length) { bisectNext(g, repo); return 1; }
    const shown = rest.map(a => "'" + a.replaceAll("'", "'\\''") + "'").join(' ');
    const ctx = g.ctx;
    for (;;) {
      g.out(subMsg('bisect', 'running', { command: shown }) + '\n');
      const code = await ctx.shell.invoke([...rest], { stdin: ctx.stdin, out: ctx.stdout, err: ctx.stderr, fds: ctx.fds });
      if (!ctx.fs.isDirectory(g.cwd)) g.cwd = ctx.shell.cwd;
      if (code < 0 || code >= 128) {
        g.err(subMsg('bisect', 'runFailed', { code: String(code), command: shown }) + '\n');
        return 127;
      }
      bisectMark(repo, code === 125 ? 'skip' : code === 0 ? 'good' : 'bad', repo.head()!);
      const result = bisectNext(g, repo);
      if (result === 'found') { g.out(subMsg('bisect', 'runFound') + '\n'); return 0; }
      if (result === 'skipped') { g.err(subMsg('bisect', 'runCannotContinue') + '\n'); return 2; }
      if (result === 'wait') return 1;
    }
  }
  g.err(subMsg('bisect', 'notAvailable', { action }) + '\n');
  return 1;
}

const HANDLERS: Record<string, (g: Git, args: string[]) => number | Promise<number>> = {
  apply: gitApply, am: gitAm, 'format-patch': gitFormatPatch, bisect: gitBisect,
  'merge-base': gitMergeBase, fsck: gitFsck,
  remote: gitRemote,
  init: gitInit, status: gitStatus, add: gitAdd, rm: gitRm, mv: gitMv, commit: gitCommit, log: gitLog, show: gitShow, diff: gitDiff,
  branch: gitBranch, checkout: gitCheckout, switch: gitSwitch, restore: gitRestore, reset: gitReset, tag: gitTag, merge: gitMerge,
  stash: gitStash, 'rev-parse': gitRevParse, 'ls-files': gitLsFiles, 'cat-file': gitCatFile, config: gitConfig, clean: gitClean,
  'rev-list': gitRevList,
};

export function registerGit(shell: Shell): void {
  shell.register('git', git);
}
