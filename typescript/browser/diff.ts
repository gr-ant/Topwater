// Line diff for the recording viewer: what a turn changed in a text file, as unified-diff hunks.

export type DiffLine = { op: ' ' | '-' | '+'; text: string; oldNo?: number; newNo?: number };
export interface Hunk { oldStart: number; oldLines: number; newStart: number; newLines: number; lines: DiffLine[] }

/** Most cells (old lines x new lines, after trimming the common ends) the table may have; past it the diff is skipped. */
const MAX_CELLS = 4_000_000;

/** Unified-diff hunks between two texts (null when they are too big to diff here). */
export function diffLines(a: string, b: string, context = 3): Hunk[] | null {
  const x = split(a), y = split(b);
  // trim the common start and end, so a small edit to a big file stays cheap
  let pre = 0;
  while (pre < x.length && pre < y.length && x[pre] === y[pre]) pre++;
  let post = 0;
  while (post < x.length - pre && post < y.length - pre && x[x.length - 1 - post] === y[y.length - 1 - post]) post++;
  const xs = x.slice(pre, x.length - post), ys = y.slice(pre, y.length - post);
  if ((xs.length + 1) * (ys.length + 1) > MAX_CELLS) return null;

  // longest common subsequence, then walk it
  const n = xs.length, m = ys.length, w = m + 1;
  const lcs = new Uint32Array((n + 1) * w);
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--)
      lcs[i * w + j] = xs[i] === ys[j] ? lcs[(i + 1) * w + j + 1] + 1 : Math.max(lcs[(i + 1) * w + j], lcs[i * w + j + 1]);
  const ops: DiffLine[] = [];
  for (let k = 0; k < pre; k++) ops.push({ op: ' ', text: x[k], oldNo: k + 1, newNo: k + 1 });
  let i = 0, j = 0;
  while (i < n || j < m) {
    if (i < n && j < m && xs[i] === ys[j]) { ops.push({ op: ' ', text: xs[i], oldNo: pre + i + 1, newNo: pre + j + 1 }); i++; j++; }
    else if (j < m && (i === n || lcs[i * w + j + 1] > lcs[(i + 1) * w + j])) { ops.push({ op: '+', text: ys[j], newNo: pre + j + 1 }); j++; }
    else { ops.push({ op: '-', text: xs[i], oldNo: pre + i + 1 }); i++; }
  }
  for (let k = 0; k < post; k++) ops.push({ op: ' ', text: x[x.length - post + k], oldNo: x.length - post + k + 1, newNo: y.length - post + k + 1 });

  // group the changes with `context` unchanged lines around each
  const hunks: Hunk[] = [];
  let k = 0;
  while (k < ops.length) {
    if (ops[k].op === ' ') { k++; continue; }
    let start = Math.max(0, k - context), end = k;
    for (;;) {
      while (end < ops.length && ops[end].op !== ' ') end++;
      let next = end;
      while (next < ops.length && ops[next].op === ' ') next++;
      if (next < ops.length && next - end <= 2 * context) { end = next; continue; }
      end = Math.min(ops.length, end + context);
      break;
    }
    const lines = ops.slice(start, end);
    const firstOld = lines.find(l => l.oldNo !== undefined)?.oldNo, firstNew = lines.find(l => l.newNo !== undefined)?.newNo;
    const oldLines = lines.filter(l => l.op !== '+').length, newLines = lines.filter(l => l.op !== '-').length;
    hunks.push({ oldStart: firstOld ?? oldBefore(ops, start), oldLines, newStart: firstNew ?? newBefore(ops, start), newLines, lines });
    k = end;
  }
  return hunks;
}

function oldBefore(ops: DiffLine[], k: number) { for (let i = k - 1; i >= 0; i--) if (ops[i].oldNo !== undefined) return ops[i].oldNo!; return 0; }
function newBefore(ops: DiffLine[], k: number) { for (let i = k - 1; i >= 0; i--) if (ops[i].newNo !== undefined) return ops[i].newNo!; return 0; }

function split(s: string): string[] {
  if (s === '') return [];
  const lines = s.split('\n');
  if (lines[lines.length - 1] === '') lines.pop();
  return lines;
}
