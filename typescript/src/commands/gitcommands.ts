// git: init, status, add, rm, mv, commit, log, show and diff. Part of the port of python/src/topwater/commands/git.py;
// see docs/architecture.md.
import { decodeBytes, bytesToText } from '../bytes.js';
import { EMPTY_TREE, MAX_WALK, SHA, Commit, DateError, Entry, GitError, cQuote, checkPath, cmpCp, cmpPath, fatal, formatDate, isBinary, isDigits, ljust, msg, myers, objectId, parseDate, parseIdent, partition, rstripChars, sortCp, sortPaths, splitLines, stripChars, type Values } from './gitcore.js';
import { posixRegexError, posixToJs } from '../regex.js';
import { type F, type Files, Opts, Repo, hasGlob, normalize, parse, same, subFatal, subMsg, union, usageError } from './git.js';
import { type Change, Git, Status, abbrev, ancestors, blobData, commitOf, commitTime, detachedName, diffFiles, indexFiles, mergeBase, missingPath, otherEntries, pathspecMatch, peel, remoteBranches, statEntries, summaryLines, trackingBrief, trackingText, treeFiles, treeLoader, tryResolve, userRegex, userSpecs, walkOrder, worktreeFiles, writeDiff, writeStat } from './gitrevisions.js';

// ---------- init ----------

export function gitInit(g: Git, args: string[]): number {
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

export function gitStatus(g: Git, args: string[]): number {
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

export function longStatus(repo: Repo, s: Status, committing = false): string {
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
  if (repo.gexists('rebase-apply/next')) out.push(M('amInProgress'), '');
  if (repo.gexists('BISECT_START')) out.push(M('bisecting', { branch: (repo.gtext('BISECT_START') ?? '').trim() }), '');
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

export function gitAdd(g: Git, args: string[]): number {
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

export function gitRm(g: Git, args: string[]): number {
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

export function gitMv(g: Git, args: string[]): number {
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

export function cleanMessage(text: string, stripComments = false): string {
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

export function readMessageFile(g: Git, path: string): string {
  if (path === '-') return g.ctx.stdinText;
  const full = normalize(g.cwd, path);
  if (!g.ctx.fs.isFile(full)) throw new GitError(subMsg('commit', 'cannotReadMessage', { file: path }), 128);
  return bytesToText(g.ctx.fs.readBytes(full));
}

export function gitCommit(g: Git, args: string[]): number {
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
export function revisionsAndPaths(repo: Repo, o: Opts): [string[], string[], string[]] {
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

export const allRefCommits = (repo: Repo) => repo.refs('refs/').map(([, s]) => peel(repo, s, 'commit')).filter((c): c is string => c !== null);
export const dedupe = (xs: string[]) => [...new Set(xs)];

export function gitLog(g: Git, args: string[]): number {
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

export function gitShow(g: Git, args: string[]): number {
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

export function gitDiff(g: Git, args: string[]): number {
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
        if (/^(<{7}|={7}|>{7})(?:[ \t\r]|$)/.test(text)) {
          bad = true;
          g.out(subMsg('diff', 'checkMarker', { file: name, line: String(j + 1) }) + '\n');
          continue;
        }
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
