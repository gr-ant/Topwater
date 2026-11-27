// git: a local version control system over the virtual filesystem (see gitcore.ts for the formats). No network, no
// remotes; the .git directory it writes is a real repository. A port of python/src/topwater/commands/git.py (the
// reference); keep them in step.
import { ShellError, specSubMessage, type CommandContext } from '../command.js';
import { VirtualFileSystem } from '../vfs.js';
import { decodeBytes, encodeText, bytesToText } from '../bytes.js';
import { zlibSync } from '../compress.js';
import { inflateRaw, OutputLimitError } from '../inflate.js';
import { COMMANDS, type CommandSpec, type SubcommandSpec } from '../spec.generated.js';
import { EMPTY_TREE, MAX_DEPTH, SHA, Commit, Config, Entry, GitError, IgnoreRules, checkPath, cmpPath, concat, fatal, isDigits, msg, objectId, parseTree, partition, readIndex, rstripChars, sortCp, treeBytes, writeIndex, type TreeEntry, type Values } from './gitcore.js';
import { MAX_NAMES, MAX_PACKS, Corrupt, ObjectCache, Pack } from './gitpack.js';
import { type Git, validHeadTarget } from './gitrevisions.js';

export const SPEC = (): CommandSpec => COMMANDS.get('git')!;
export const subSpec = (sub: string): SubcommandSpec => SPEC().subcommands!.get(sub)!;
export const normalize = (cwd: string, p: string) => VirtualFileSystem.normalize(cwd, p);

export const subMsg = (sub: string, key: string, values: Values = {}) => specSubMessage('git', sub, key, values);
export const subFatal = (sub: string, key: string, values: Values = {}, code = 128) => new GitError(subMsg(sub, key, values), code);

/** A file as a tree or the index holds it: [mode, blob id]. */
export type F = [mode: string, sha: string];
export type Files = Map<string, F>;
export const same = (x: F | undefined | null, y: F | undefined | null) => (x ?? null) === (y ?? null) || (!!x && !!y && x[0] === y[0] && x[1] === y[1]);
export const union = (...xs: Iterable<string>[]) => { const s = new Set<string>(); for (const x of xs) for (const v of x) s.add(v); return s; };
export const hasGlob = (s: string) => /[*?[]/.test(s);

// ---------- option parsing ----------

export class Opts {
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

export function usageError(sub: string, text: string): GitError {
  return new GitError(text + '\n' + rstripChars(subSpec(sub).help, '\n'), 129);
}

export function parse(sub: string, args: string[]): Opts {
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

export class Repo {
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
    if (!/^[A-Za-z0-9_./-]+$/.test(name) || name.includes('..') || name.split('/').some(p => p === '' || p === '.')) return null; // refs/heads/. (from `git checkout .`) names no ref
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
