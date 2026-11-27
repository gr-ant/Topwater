// git: apply, am, format-patch and bisect, and the table of subcommands. Part of the port of
// python/src/topwater/commands/git.py; see docs/architecture.md.
import { VirtualFileSystem } from '../vfs.js';
import { decodeBytes, encodeText } from '../bytes.js';
import { EMPTY_TREE, Entry, GitError, cQuote, checkPath, concat, cpLen, fatal, formatDate, isDigits, ljust, objectId, parseIdent, partition, rstripChars, splitLines, stripChars } from './gitcore.js';
import { Opts, Repo, SPEC, normalize, parse, subFatal, subMsg, subSpec } from './git.js';
import { Git, ancestors, commitOf, diffFiles, indexFiles, peel, renameLabel, treeFiles, tryResolve, walkOrder, writeDiff } from './gitrevisions.js';
import { gitAdd, gitCommit, gitDiff, gitInit, gitLog, gitMv, gitRm, gitShow, gitStatus } from './gitcommands.js';
import { gitBranch, gitCatFile, gitCheckout, gitClean, gitConfig, gitFsck, gitLsFiles, gitMerge, gitMergeBase, gitRemote, gitReset, gitRestore, gitRevList, gitRevParse, gitStash, gitSwitch, gitTag, moveHead, switchTrees } from './gitmore.js';

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

/** Every index at or after pos where pre occurs in lines, in linear time (Knuth-Morris-Pratt over the lines), so
 * a file of identical lines cannot make the search quadratic. */
function findLines(lines: string[], pre: string[], pos: number, check: () => void): number[] {
  const hits: number[] = [];
  if (!pre.length) { for (let i = pos; i <= lines.length; i++) hits.push(i); return hits; }
  const fail = new Array<number>(pre.length).fill(0);
  let k = 0;
  for (let i = 1; i < pre.length; i++) {
    while (k && pre[i] !== pre[k]) k = fail[k - 1];
    if (pre[i] === pre[k]) k++;
    fail[i] = k;
  }
  k = 0;
  for (let i = pos; i < lines.length; i++) {
    if (i % 4096 === 0) check();
    while (k && lines[i] !== pre[k]) k = fail[k - 1];
    if (lines[i] === pre[k]) k++;
    if (k === pre.length) { hits.push(i - k + 1); k = fail[k - 1]; }
  }
  return hits;
}

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
    let found: number | null = null; // the nearest hit; of two as near, the later one
    for (const p of findLines(lines, pre, pos, g.check))
      if (found === null || Math.abs(p - start) < Math.abs(found - start) || (Math.abs(p - start) === Math.abs(found - start) && found < start && p >= start)) found = p;
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
      if (fs.isSymlink(full)) return ['120000', encodeText(fs.readLink(full))];
      if (fs.isFile(full)) return [fs.stat(full).mode & 0o100 ? '100755' : '100644', fs.readBytes(full)];
      return null;
    }
    const e = entries.get(p);
    return e ? [e.mode, repo!.typed(e.sha, 'blob')] : null;
  };
  /** Whether a directory above p is a symbolic link (or a file or link these patches make), which git refuses
   * to write through. */
  const beyondLink = (p: string) => {
    const parts = p.split('/').slice(0, -1);
    for (let k = 1; k <= parts.length; k++) {
      const above = parts.slice(0, k).join('/');
      if (state.get(above) || (worktree && fs.isSymlink(rstripChars(top, '/') + '/' + above))) return true;
    }
    return false;
  };
  let ok = true;
  for (const fp of patches) {
    const path = fp.neu || fp.old || '';
    for (const p of [fp.old, fp.neu]) {
      if (p === null) continue;
      try { checkPath(p); } catch (e) { // both sides: a rename must not reach outside the work tree either
        if (e instanceof GitError) throw subFatal('apply', 'invalidPath', { path: p });
        throw e;
      }
    }
    if (verbose) g.err(subMsg('apply', 'checking', { path }) + '\n');
    if (fp.binary) {
      g.err(subMsg('apply', 'binary', { path }) + '\n');
      ok = false;
      continue;
    }
    if (fp.oldMode && fp.newMode && (fp.oldMode === '120000') !== (fp.newMode === '120000')) {
      g.err(subMsg('apply', 'modeMismatch', { path, new: fp.newMode, old: fp.oldMode }) + '\n');
      ok = false;
      continue;
    }
    const linked = [fp.old, fp.neu].find(p => p !== null && beyondLink(p));
    if (linked) {
      g.err(subMsg('apply', 'beyondLink', { path: linked }) + '\n');
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
          g.err(subMsg('apply', index ? 'notInIndex' : 'noSuchFile', { path: fp.old }) + '\n');
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
    if (fp.neu !== null) {
      let mode = fp.newMode || oldMode || '100644';
      if (mode !== '120000') mode = /^[0-7]+$/.test(mode) && parseInt(mode, 8) & 0o111 ? '100755' : '100644'; // any executable bit makes an executable file, as git reads modes
      state.set(fp.neu, [mode, newData]);
    }
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
        if (fs.isSymlink(full) || (v[0] === '120000' && fs.exists(full, false))) fs.delete(full); // a link is replaced, never written through
        if (v[0] === '120000') fs.createSymlink(full, decodeBytes(v[1]));
        else {
          fs.writeBytes(full, v[1]);
          fs.chmod(full, (v[0] === '100755' ? 0o777 : 0o666) & ~fs.umask);
        }
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

/** RFC 2047 encoded words (=?UTF-8?q?J=C3=BCrgen?=, =?UTF-8?b?...?=) in a mail header, as git format-patch writes
 * non-ASCII names and subjects; the space between two encoded words goes. */
function decodeWords(value: string): string {
  const word = String.raw`=\?([^?\s]+)\?([QqBb])\?([^?\s]*)\?=`;
  value = value.replace(new RegExp(`(${word})\\s+(?=${word})`, 'g'), '$1');
  return value.replace(new RegExp(word, 'g'), (all, charset: string, kind: string, text: string) => {
    let raw: Uint8Array;
    if (kind.toLowerCase() === 'b') {
      const t = text + '='.repeat((4 - text.length % 4) % 4);
      try { raw = Uint8Array.from(atob(t.replace(/[^A-Za-z0-9+/=]/g, '')), c => c.charCodeAt(0)); } catch { return all; }
    } else {
      const t = text.replaceAll('_', ' ');
      if ([...t].some(c => c.charCodeAt(0) > 255)) return all;
      const bytes: number[] = [];
      for (let i = 0; i < t.length; i++) {
        const h = /^=([0-9A-Fa-f]{2})/.exec(t.slice(i, i + 3));
        if (h) { bytes.push(parseInt(h[1], 16)); i += 2; } else bytes.push(t.charCodeAt(i));
      }
      raw = Uint8Array.from(bytes);
    }
    const latin = ['iso-8859-1', 'latin-1'].includes(charset.toLowerCase());
    return new TextDecoder(latin ? 'latin1' : 'utf-8').decode(raw); // unknown charsets as UTF-8; bad bytes become U+FFFD
  });
}

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
  for (const k of ['from', 'subject']) if (headers.has(k)) headers.set(k, decodeWords(headers.get(k)!));
  const from = headers.get('from') ?? '';
  const m = /^\s*(.*?)\s*<([^<>]*)>\s*$/.exec(from);
  const [name, email] = m ? [m[1].replaceAll('"', ''), m[2]] : [from, from];
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
    const inside = new Map(order.map(c => [c, repo.commit(c).parents.filter(p => candidates.has(p))]));
    const linear = [...inside.values()].every(ps => ps.length <= 1);
    const weight = new Map<string, number>();
    for (const c of order) { // oldest first, so a parent's count is known
      const ps = inside.get(c)!;
      if (linear && ps.every(p => weight.has(p))) weight.set(c, 1 + (ps.length ? weight.get(ps[0])! : 0)); // itself plus what its one parent reaches: linear time
      else {
        // shortcut: with merges, counts reachable candidates per commit (quadratic); fine for the histories a machine holds
        let w = 0;
        for (const x of ancestors(repo, [c], check)) if (candidates.has(x)) w++;
        weight.set(c, w);
      }
      check();
    }
    for (const c of order) {
      if (skipped.has(c) || c === bad) continue;
      const w = weight.get(c)!;
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
    bisectClear(repo); // marks left without a session (bisect reset run from bisect run) go too
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
      if (!repo.gexists('BISECT_START')) return 1; // the command ended the session (git bisect reset): nothing left to mark
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

export const HANDLERS: Record<string, (g: Git, args: string[]) => number | Promise<number>> = {
  apply: gitApply, am: gitAm, 'format-patch': gitFormatPatch, bisect: gitBisect,
  'merge-base': gitMergeBase, fsck: gitFsck,
  remote: gitRemote,
  init: gitInit, status: gitStatus, add: gitAdd, rm: gitRm, mv: gitMv, commit: gitCommit, log: gitLog, show: gitShow, diff: gitDiff,
  branch: gitBranch, checkout: gitCheckout, switch: gitSwitch, restore: gitRestore, reset: gitReset, tag: gitTag, merge: gitMerge,
  stash: gitStash, 'rev-parse': gitRevParse, 'ls-files': gitLsFiles, 'cat-file': gitCatFile, config: gitConfig, clean: gitClean,
  'rev-list': gitRevList,
};
