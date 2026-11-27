// diff: line and directory comparison; registered by text.ts. See docs/architecture.md.
import { Args, specOf, specMessage, ShellError, UsageException, splitLines, type CommandContext } from '../command.js';
import { VirtualFileSystem } from '../vfs.js';
import { buffered, checkEvery } from './text.js';

interface DOp { t: 'eq' | 'del' | 'ins'; a: number; b: number }

function diffOps(ctx: CommandContext, x: string[], y: string[]): DOp[] {
  if (x.length * y.length > 25_000_000) throw new ShellError(specMessage('diff', 'tooLarge', {}), 2);
  const w = y.length + 1;
  const lcs = new Int32Array((x.length + 1) * w);
  for (let i = x.length - 1; i >= 0; i--) {
    checkEvery(ctx, i);
    for (let j = y.length - 1; j >= 0; j--) lcs[i * w + j] = x[i] === y[j] ? lcs[(i + 1) * w + j + 1] + 1 : Math.max(lcs[(i + 1) * w + j], lcs[i * w + j + 1]);
  }
  const ops: DOp[] = [];
  let i = 0, j = 0;
  while (i < x.length || j < y.length) {
    if (i < x.length && j < y.length && x[i] === y[j]) ops.push({ t: 'eq', a: i++, b: j++ });
    else if (j < y.length && (i === x.length || lcs[i * w + j + 1] >= lcs[(i + 1) * w + j])) ops.push({ t: 'ins', a: i, b: j++ });
    else ops.push({ t: 'del', a: i++, b: j });
  }
  // deletions before insertions inside each change block, like GNU diff
  for (let k = 0; k < ops.length;) {
    if (ops[k].t === 'eq') { k++; continue; }
    let e = k;
    while (e < ops.length && ops[e].t !== 'eq') e++;
    const block = ops.slice(k, e);
    ops.splice(k, block.length, ...block.filter(o => o.t === 'del'), ...block.filter(o => o.t === 'ins'));
    k = e;
  }
  return ops;
}

const dRange = (start: number, count: number) => (count === 1 ? `${start}` : `${start},${start + count - 1}`);

function diffNormal(ops: DOp[], x: string[], y: string[]): string {
  let out = '';
  for (let i = 0; i < ops.length;) {
    if (ops[i].t === 'eq') { i++; continue; }
    let j = i;
    while (j < ops.length && ops[j].t !== 'eq') j++;
    const block = ops.slice(i, j);
    const dels = block.filter(o => o.t === 'del'), ins = block.filter(o => o.t === 'ins');
    const aStart = Math.min(...block.map(o => o.a)), bStart = Math.min(...block.map(o => o.b));
    if (dels.length && ins.length) out += `${dRange(aStart + 1, dels.length)}c${dRange(bStart + 1, ins.length)}\n`;
    else if (dels.length) out += `${dRange(aStart + 1, dels.length)}d${bStart}\n`;
    else out += `${aStart}a${dRange(bStart + 1, ins.length)}\n`;
    for (const d of dels) out += '< ' + x[d.a] + '\n';
    if (dels.length && ins.length) out += '---\n';
    for (const n of ins) out += '> ' + y[n.b] + '\n';
    i = j;
  }
  return out;
}

function diffUnified(ops: DOp[], x: string[], y: string[], an: string, bn: string, context: number): string {
  let out = `--- ${an}\n+++ ${bn}\n`;
  const changes = ops.flatMap((o, i) => (o.t !== 'eq' ? [i] : []));
  const rng = (start: number, len: number) => (len === 1 ? `${start}` : `${start},${len}`);
  for (let c = 0; c < changes.length;) {
    const first = changes[c];
    let last = first;
    while (c + 1 < changes.length && changes[c + 1] - last <= 2 * context + 1) last = changes[++c];
    c++;
    const hunk = ops.slice(Math.max(0, first - context), Math.min(ops.length, last + context + 1));
    const aLen = hunk.filter(o => o.t !== 'ins').length, bLen = hunk.filter(o => o.t !== 'del').length;
    const aStart = Math.min(...hunk.map(o => o.a)) + (aLen > 0 ? 1 : 0), bStart = Math.min(...hunk.map(o => o.b)) + (bLen > 0 ? 1 : 0);
    out += `@@ -${rng(aStart, aLen)} +${rng(bStart, bLen)} @@\n`;
    for (const o of hunk) out += (o.t === 'eq' ? ' ' + x[o.a] : o.t === 'del' ? '-' + x[o.a] : '+' + y[o.b]) + '\n';
  }
  return out;
}

export function diff(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, specOf('diff'));
  if (a.positional.length !== 2) throw new UsageException(a.positional.length < 2 ? specMessage('diff', 'missingOperandAfter', { operand: a.positional[a.positional.length - 1] ?? 'diff' }) : specMessage('diff', 'extraOperand', { operand: a.positional[2] }));
  const key = (l: string) => {
    if (a.has('w')) l = l.replace(/\s+/g, '');
    else if (a.has('b')) l = l.replace(/\s+$/, '').replace(/\s+/g, ' ');
    return a.has('i') ? l.toLowerCase() : l;
  };
  const fs = ctx.fs;
  const read = (p: string): string => {
    if (p === '-') return ctx.stdinText;
    const abs = ctx.resolvePath(p);
    if (!fs.exists(abs)) {
      if (a.has('N')) return '';
      throw new ShellError(specMessage('diff', 'fileNotFound', { file: p }), 2);
    }
    if (fs.isDirectory(abs)) throw new ShellError(`${p}: Is a directory`, 2);
    return fs.readPipeText(abs);
  };
  const context = a.value('U') !== undefined ? parseInt(a.value('U')!, 10) : 3;
  const out = buffered(ctx);
  const join = (dir: string, name: string) => dir.endsWith('/') ? dir + name : dir + '/' + name;
  // the line GNU diff prints before each differing pair in a directory: the options as typed, then both names
  const header = (x: string, y: string) => {
    const options = [...ctx.args];
    for (const operand of a.positional) { const k = options.indexOf(operand); if (k >= 0) options.splice(k, 1); }
    return ['diff', ...options, x, y].join(' ');
  };
  const files = (an: string, bn: string, head: string | null): number => {
    const x = splitLines(read(an)), y = splitLines(read(bn));
    const ops = diffOps(ctx, x.map(key), y.map(key));
    if (ops.every(o => o.t === 'eq')) { if (a.has('s')) out.w(`Files ${an} and ${bn} are identical\n`); return 0; }
    if (a.has('q')) { out.w(`Files ${an} and ${bn} differ\n`); return 1; }
    if (head !== null) out.w(head + '\n');
    out.w(a.has('u') || a.value('U') !== undefined ? diffUnified(ops, x, y, an, bn, context) : diffNormal(ops, x, y));
    return 1;
  };
  // two directories: entries only one side has, then each common one in name order (subdirectories with -r)
  const dirs = (an: string, bn: string, depth: number): number => {
    if (depth > 64) throw new ShellError(specMessage('diff', 'tooLarge', {}), 2); // shortcut: a symlink loop ends here, not with GNU's loop message
    const pa = ctx.resolvePath(an), pb = ctx.resolvePath(bn);
    const left = new Set(fs.list(pa)), right = new Set(fs.list(pb));
    let code = 0;
    for (const name of [...new Set([...left, ...right])].sort()) {
      const x = join(an, name), y = join(bn, name);
      const inA = left.has(name), inB = right.has(name);
      const dirA = inA && fs.isDirectory(join(pa, name)), dirB = inB && fs.isDirectory(join(pb, name));
      let rc: number;
      if (!inA || !inB) {
        if (a.has('N') && !(dirA || dirB)) rc = files(x, y, header(x, y));
        else { out.w(specMessage('diff', 'onlyIn', { dir: inA ? an : bn, name }) + '\n'); rc = 1; }
      } else if (dirA && dirB) {
        if (a.has('r')) rc = dirs(x, y, depth + 1);
        else { out.w(specMessage('diff', 'commonSubdirectories', { file1: x, file2: y }) + '\n'); rc = 0; }
      } else if (dirA || dirB) {
        out.w(specMessage('diff', 'typeMismatch', { file1: x, kind1: dirA ? 'directory' : 'regular file', file2: y, kind2: dirB ? 'directory' : 'regular file' }) + '\n');
        rc = 1;
      } else rc = files(x, y, header(x, y));
      code = Math.max(code, rc);
    }
    return code;
  };
  try {
    let [an, bn] = a.positional;
    for (const p of [an, bn])
      if (p !== '-' && !a.has('N') && !fs.exists(ctx.resolvePath(p))) throw new ShellError(specMessage('diff', 'fileNotFound', { file: p }), 2);
    const isDir = (p: string) => p !== '-' && fs.isDirectory(ctx.resolvePath(p));
    if (isDir(an) && isDir(bn)) return dirs(an, bn, 0);
    // a directory and a file: compare the file with the one of the same name in the directory
    if (isDir(an)) an = join(an, VirtualFileSystem.getName(bn));
    else if (isDir(bn)) bn = join(bn, VirtualFileSystem.getName(an));
    return files(an, bn, null);
  } finally { out.flush(); }
}
