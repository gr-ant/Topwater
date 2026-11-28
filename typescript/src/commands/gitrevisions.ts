// git: revisions, the command (the Git context, the dispatcher and registration) and the shared pieces: comparing trees,
// the index and the work tree. Part of the port of python/src/topwater/commands/git.py; see docs/architecture.md.
import type { Shell } from '../shell.js';
import { ShellError, type CommandContext } from '../command.js';
import { VirtualFileSystem } from '../vfs.js';
import { bytesToText } from '../bytes.js';
import { HEX, MAX_WALK, SHA, Config, DateError, Entry, GitError, cQuote, canonicalKey, cmpPath, countChanges, cpLen, cpSlice, fatal, globFull, isBinary, isDigits, ljust, lstripChars, msg, parseDate, parseIdent, partition, rjust, rstripChars, sortPaths, splitLines, stripChars, unified, validKey, type Values } from './gitcore.js';
import { type F, type Files, Opts, Repo, SPEC, hasGlob, normalize, same, subMsg, union } from './git.js';
import { HANDLERS } from './gitpatch.js';

// ---------- revisions ----------

/** A revision -> object id. Raises badRevision when it names nothing. */
export function resolve(repo: Repo, spec: string): string {
  const sha = tryResolve(repo, spec);
  if (sha === null) {
    missingPath(repo, spec);
    throw fatal('badRevision', { rev: spec });
  }
  return sha;
}

/** rev:path where rev exists but path does not gets its own message. */
export function missingPath(repo: Repo, spec: string): void {
  const [rev, colon, path] = partition(spec, ':');
  if (colon && rev && tryResolve(repo, rev) !== null) throw fatal('pathNotInRevision', { path, rev });
}

export function tryResolve(repo: Repo, spec: string): string | null {
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
export function detachedName(repo: Repo): [boolean, string] {
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

export function previousBranch(repo: Repo, n: number): string | null {
  const lines = (repo.gtext('logs/HEAD') ?? '').split('\n');
  let seen = 0;
  for (let i = lines.length - 1; i >= 0; i--) {
    const m = /\tcheckout: moving from (\S+) to (\S+)$/.exec(lines[i]);
    if (m && ++seen === n) return m[1];
  }
  return null;
}

/** Follows tags (and commit -> tree) until an object of kind want ('commit', 'tree', 'any' = not a tag). */
export function peel(repo: Repo, sha: string | null, want: string): string | null {
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

export function commitOf(repo: Repo, spec: string): string {
  const sha = peel(repo, resolve(repo, spec), 'commit');
  if (sha === null) throw fatal('notCommit', { rev: spec });
  return sha;
}

export const treeFiles = (repo: Repo, commit: string | null | undefined): Files => commit ? repo.flatten(repo.commit(commit).tree) : new Map();

export function ancestors(repo: Repo, start: Iterable<string>, check: () => void): Set<string> {
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
export function mergeBase(repo: Repo, a: string, b: string, check: () => void): string | null {
  const bs = ancestors(repo, [b], check);
  const common = [...ancestors(repo, [a], check)].filter(c => bs.has(c));
  if (!common.length) return null;
  const below = ancestors(repo, common.flatMap(c => repo.commit(c).parents), check);
  const best = common.filter(c => !below.has(c)).sort();
  return best.length ? walkOrder(repo, best, check)[0] : null;
}

/** Commits reachable from starts, newest commit date first (ties: first seen first), each once. */
/** [name to show, ref] of a branch's upstream, from branch.<name>.remote and .merge (as git clone sets them). */
export function upstream(repo: Repo, branch: string): [string, string] | null {
  const cfg = repo.config();
  const remote = cfg.get(`branch.${branch}.remote`), merge = cfg.get(`branch.${branch}.merge`);
  if (!remote || !merge || !merge.startsWith('refs/heads/')) return null;
  if (remote === '.') return [merge.slice(11), merge];
  // shortcut: assumes the default fetch refspec (refs/remotes/<remote>/*) that clone writes; read remote.<name>.fetch if others show up
  return [`${remote}/${merge.slice(11)}`, `refs/remotes/${remote}/${merge.slice(11)}`];
}

/** The branch <name>@{u} asks about (HEAD and '' mean the current one); refuses as git does when there is none. */
export function upstreamBranch(repo: Repo, name: string): string {
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
export function trackingText(repo: Repo, branch: string, check: () => void): string | null {
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
export function trackingBrief(repo: Repo, branch: string, check: () => void): [string, string] | null {
  const t = tracking(repo, branch, check);
  if (t === null) return null;
  const [name, ahead, behind, gone] = t;
  const M = (key: string, v: Values = {}) => subMsg('status', key, v);
  if (gone) return [name, M('trackGone')];
  return [name, [...(ahead ? [M('trackAhead', { n: String(ahead) })] : []), ...(behind ? [M('trackBehind', { n: String(behind) })] : [])].join(', ')];
}

/** [name below refs/remotes/, commit, symbolic target below refs/remotes/ or null] sorted by name. */
export function remoteBranches(repo: Repo): [string, string | null, string | null][] {
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

export function walkOrder(repo: Repo, starts: string[], check: () => void, limitSet: Set<string> | null = null): string[] {
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

export const commitTime = (repo: Repo, sha: string) => parseIdent(repo.commit(sha).committer)[2];

// ---------- the command ----------

export const validHeadTarget = (target: string) => target.startsWith('refs/') && target.split('/').every(p => p !== '' && p !== '.' && p !== '..');

/** A HEAD that names neither a commit nor a ref under refs/ makes the directory not a repository, as in git. */
function validHead(fs: VirtualFileSystem, path: string): boolean {
  if (fs.stat(path).size > 4096) return false;
  const text = bytesToText(fs.readBytes(path)).trim();
  return SHA.test(text) || (text.startsWith('ref:') && validHeadTarget(text.slice(4).trim()));
}

/** Newlines and angle brackets would break the commit header, so they go, as in git. */
const cleanIdent = (s: string) => s.replace(/[<>\n\r]/g, '').trim();

/** One git invocation: the shell context, the directory it works from (after -C) and -c overrides. */
export class Git {
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
export function pathspecMatch(specs: string[] | null, path: string): boolean {
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
export function userRegex(pattern: string | null): RegExp | null {
  if (!pattern) return null;
  try { return new RegExp(pattern); } catch { throw fatal('badRegex', { pattern }); }
}

export const userSpecs = (repo: Repo, items: string[]): string[] | null => items.length ? items.map(p => repo.fromUser(p)) : null;

export type Change = [status: string, old: string | null, neu: string | null];
const kindOf = (m: string) => m === '120000' ? 'l' : 'f';

/** [status, old path, new path][] between two path -> [mode, sha] maps, sorted by path. A removed and an added path with
 * the same contents pair up as a rename ("R"); with load(side, path), so do ones at least half alike ("R075"). */
export function diffFiles(a: Files, b: Files, specs: string[] | null = null, load?: (side: string, path: string) => Uint8Array): Change[] {
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
export const treeLoader = (repo: Repo, a: Files, b: Files) => (side: string, p: string) => repo.typed((side === 'a' ? a : b).get(p)![1], 'blob');

export function worktreeFiles(repo: Repo, paths: Iterable<string>): Files {
  const out: Files = new Map();
  for (const p of paths) { const w = repo.wtId(p); if (w !== null) out.set(p, w); }
  return out;
}

export const indexFiles = (entries: Iterable<Entry>): Files => {
  const out: Files = new Map();
  for (const e of entries) if (e.stage === 0) out.set(e.path, [e.mode, e.sha]);
  return out;
};

export class Status {
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

export function untracked(repo: Repo, tracked: Set<string>, mode: string): string[] {
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

export function blobData(repo: Repo, side: F | null | undefined, path: string, fromWorktree: boolean): Uint8Array {
  if (!side) return new Uint8Array(0);
  if (fromWorktree) { const s = repo.wtStat(path); return s ? s[1] : new Uint8Array(0); }
  return repo.typed(side[1], 'blob');
}

export const abbrev = (sha: string) => sha.slice(0, 7);
const eqBytes = (x: Uint8Array, y: Uint8Array) => x.length === y.length && x.every((v, i) => v === y[i]);

/** 'a => b', or with the shared directories factored out: 'dir/{a => b}/f'. */
export function renameLabel(a: string, b: string): string {
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
export function writeDiff(g: Git, repo: Repo, a: Files, b: Files, opts: Opts, specs: string[] | null = null, bWorktree = false, aWorktree = false,
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
    g.ctx.checkTime(); // each file is split into lines: a commit can name one big blob at many paths
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

export function writeStat(write: Write, stats: Stat[], short = false): void {
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
  g.ctx.checkTime();
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

export function summaryLines(changes: Change[], a: Files, b: Files): string[] {
  const out: string[] = [];
  for (const [st, old, neu] of changes) {
    if (st === 'A') out.push(` create mode ${b.get(neu!)![0]} ${cQuote(neu!)}`);
    else if (st === 'D') out.push(` delete mode ${a.get(old!)![0]} ${cQuote(old!)}`);
    else if (st[0] === 'R') out.push(` rename ${renameLabel(old!, neu!)} (${score(st)}%)`);
    else if (a.get(old!)![0] !== b.get(neu!)![0] && st === 'M') out.push(` mode change ${a.get(old!)![0]} => ${b.get(neu!)![0]} ${cQuote(neu!)}`);
  }
  return out;
}

export function statEntries(g: Git, repo: Repo, a: Files, b: Files, changes: Change[]): Stat[] {
  return changes.map(([st, old, neu]): Stat => {
    const x = old ? repo.typed(a.get(old)![1], 'blob') : new Uint8Array(0);
    const y = neu ? repo.typed(b.get(neu)![1], 'blob') : new Uint8Array(0);
    if (isBinary(x) || isBinary(y)) return [st, old, neu, null, x.length, y.length];
    return [st, old, neu, eqBytes(x, y) ? [0, 0] : countChanges(x, y, g.check), x.length, y.length];
  });
}

export const otherEntries = (unmerged: Map<string, Map<number, Entry>>) => [...unmerged.values()].flatMap(st => [...st.values()]);

export function registerGit(shell: Shell): void {
  shell.register('git', git);
}
