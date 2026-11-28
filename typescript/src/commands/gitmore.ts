// git: checkout, switch, restore, reset, branch, tag, merge, stash, the plumbing and inspection commands, and config.
// Part of the port of python/src/topwater/commands/git.py; see docs/architecture.md.
import { decodeBytes, encodeText, bytesToText } from '../bytes.js';
import { MAX_WALK, Entry, GitError, cQuote, canonicalKey, configBool, cpLen, fatal, globFull, isBinary, isDigits, ljust, mergeLines, msg, parseTree, partition, rjust, rstripChars, sortCp, sortPaths, validKey, type Values } from './gitcore.js';
import { type F, type Files, Repo, normalize, parse, same, subFatal, subMsg, subSpec, union, usageError } from './git.js';
import { Git, Status, abbrev, ancestors, commitOf, detachedName, diffFiles, indexFiles, mergeBase, otherEntries, pathspecMatch, peel, previousBranch, remoteBranches, resolve, statEntries, summaryLines, trackingBrief, trackingText, treeFiles, treeLoader, tryResolve, untracked, upstream, upstreamBranch, userSpecs, walkOrder, writeDiff, writeStat } from './gitrevisions.js';
import { allRefCommits, cleanMessage, dedupe, gitCommit, longStatus, readMessageFile, revisionsAndPaths } from './gitcommands.js';

// ---------- moving between commits ----------

function validBranchName(name: string): boolean {
  if (!name || name === 'HEAD' || name === '@' || /^[-/.]/.test(name) || ['/', '.', '.lock'].some(x => name.endsWith(x))) return false;
  if (name.includes('..') || name.includes('//') || name.includes('@{') || name.includes('/.')) return false;
  return ![...' ~^:?*[\\\x7f'].some(c => name.includes(c)) && ![...name].some(c => c.charCodeAt(0) < 32);
}

/** Moves the index and work tree from commit old to commit new, keeping local changes to paths the two do not differ
 * in. Refuses (exit 1) when that would lose a local change or an untracked file. Returns the paths that stay changed. */
export function switchTrees(g: Git, repo: Repo, old: string | null, neu: string | null, force: boolean, verb = 'checkout'): string[] {
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

export function moveHead(g: Git, repo: Repo, targetRef: string | null, targetSha: string | null, label: string, force: boolean, quiet: boolean,
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

export function gitCheckout(g: Git, args: string[]): number {
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
  if (!o.has('\x01') && validBranchName(target) && repo.readRef('refs/heads/' + target) && o.args.length === 1)
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

export function gitSwitch(g: Git, args: string[]): number {
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

export function gitRestore(g: Git, args: string[]): number {
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

export function gitReset(g: Git, args: string[]): number {
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

export function gitBranch(g: Git, args: string[]): number {
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

export function gitTag(g: Git, args: string[]): number {
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

export function gitMerge(g: Git, args: string[]): number {
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

export function gitStash(g: Git, args: string[]): number {
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

export function gitRevParse(g: Git, args: string[]): number {
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

export function gitLsFiles(g: Git, args: string[]): number {
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

export function gitCatFile(g: Git, args: string[]): number {
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

export function gitRevList(g: Git, args: string[]): number {
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

export function gitClean(g: Git, args: string[]): number {
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

export function gitConfig(g: Git, args: string[]): number {
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

export function gitRemote(g: Git, args: string[]): number {
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

export function gitMergeBase(g: Git, args: string[]): number {
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

export function gitFsck(g: Git, args: string[]): number {
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
  let linked = 0; // all kept until the end: as many as objects one walk may visit
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
    if ((linked += links.get(sha)?.length ?? 0) > MAX_WALK) throw fatal('tooLarge', { what: 'the object graph' });
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
