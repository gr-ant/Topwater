// Recording viewer: steps through a Topwater recording turn by turn, compares any two moments, and continues from any
// turn in a terminal whose commands become a branch of the timeline. The recording and its branches live in a Web
// Worker (replay-worker.ts), which rebuilds the machine as of any turn; this page only renders what it answers.
import { FORK_PREFIX, dropStaleForks, forkKey } from '../fork-handoff.js';
import type { CallInfo, Change, DiffView, FileView, LoadedRecording, Point, ReplayReply, ReplayRequest, TreeEntry, TurnView, WebBody } from '../replay-protocol.js';

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const statusEl = $('status'), viewer = $('viewer'), slider = $<HTMLInputElement>('slider'), pos = $('pos');
const timeline = $('timeline'), turnEl = $('turn'), treeEl = $('tree'), fileEl = $('fileview'), changesEl = $('changes');
const branchesEl = $('branches'), cmpA = $<HTMLSelectElement>('cmp-a'), cmpZ = $<HTMLSelectElement>('cmp-z');
const liveEl = $('live'), liveOut = $('live-out'), liveCmd = $<HTMLInputElement>('live-cmd'), liveStop = $<HTMLButtonElement>('live-stop');
const REQUEST_TIMEOUT_MS = 60_000;

// ---------- the worker ----------
let worker: Worker;
let nextId = 1;
const pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
let loaded: { data: Uint8Array; sidecars: Record<string, Uint8Array>; name: string } | null = null;

function startWorker() {
  worker = new Worker(new URL('replay-worker.js', import.meta.url), { type: 'module' });
  worker.onmessage = (e: MessageEvent<ReplayReply>) => {
    const p = pending.get(e.data.id);
    pending.delete(e.data.id);
    if (!p) return;
    if (e.data.ok) p.resolve(e.data.value); else p.reject(new Error(e.data.error));
  };
}
startWorker();

type Req = ReplayRequest extends infer R ? R extends { id: number } ? Omit<R, 'id'> : never : never;
/** set while a replaced worker loads the recording and its branches again: a typed command waits for it */
let restoring: Promise<unknown> = Promise.resolve();
function request<T>(msg: Req, transfer: Transferable[] = [], what = 'rebuilding this turn', timeoutMs = REQUEST_TIMEOUT_MS): Promise<T> {
  // a replaced worker answers nothing else until it has the recording and the branches again
  return msg.t === 'load' || msg.t === 'restore' ? send<T>(msg, transfer, what, timeoutMs) : restoring.then(() => send<T>(msg, transfer, what, timeoutMs));
}

function send<T>(msg: Req, transfer: Transferable[], what: string, timeoutMs: number): Promise<T> {
  const id = nextId++;
  return new Promise<T>((resolve, reject) => {
    const timer = setTimeout(() => {
      if (!pending.delete(id)) return;
      replaceWorker('cancelled');
      reject(new Error(`${what} took longer than ${timeoutMs / 1000} s, so it was stopped`));
    }, timeoutMs);
    pending.set(id, {
      resolve: v => { clearTimeout(timer); resolve(v as T); },
      reject: e => { clearTimeout(timer); reject(e); },
    });
    worker.postMessage({ ...msg, id } as ReplayRequest, transfer);
  });
}

/** Re-running a recorded command can't be interrupted from inside: a stuck worker (or one the user stopped) is replaced,
 * every answer it owed fails with `why`, and the new one loads the recording and the branches typed so far. */
function replaceWorker(why: string) {
  worker.terminate();
  for (const p of pending.values()) p.reject(new Error(why));
  pending.clear();
  startWorker();
  if (!loaded) return;
  const again = loaded;
  restoring = (async () => {
    await request({ t: 'load', data: again.data, sidecars: again.sidecars }).catch(() => undefined);
    // the new worker rebuilds the branches' machines by re-running them
    await request({ t: 'restore', branches: branches.slice(1) }).catch(() => undefined);
  })();
}

// ---------- helpers (output is untrusted: always textContent) ----------
function el<K extends keyof HTMLElementTagNameMap>(tag: K, props: Partial<HTMLElementTagNameMap[K]> & { cls?: string } = {}, ...children: (Node | string)[]) {
  const e = document.createElement(tag);
  const { cls, ...rest } = props;
  if (cls) e.className = cls;
  Object.assign(e, rest);
  e.append(...children);
  return e;
}
const status = (text: string, error = false) => { statusEl.textContent = text; statusEl.classList.toggle('error', error); };
const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? '' : 's'}`;
const fmtTime = (ms: number) => new Date(ms).toISOString().replace('T', ' ').replace(/\.\d+Z$/, ' UTC');
const fmtSize = (n: number) => n < 1024 ? `${n} B` : n < 1 << 20 ? `${(n / 1024).toFixed(1)} KB` : `${(n / (1 << 20)).toFixed(1)} MB`;
const modeText = (isDir: boolean, mode: number) => (isDir ? 'd' : '-') + [6, 3, 0].map(s => ['r', 'w', 'x'].map((c, k) => (mode >> s) & (4 >> k) ? c : '-').join('')).join('');
const turnLabel = (index: number) => index < 0 ? 'Start' : `Turn ${index + 1}`;

// ---------- timelines: 0 is the recording; a branch continues from call `at` of its parent ----------
interface BranchInfo { parent: number; at: number; calls: CallInfo[] }
let branches: BranchInfo[] = [];
const len = (b: number) => b === 0 ? branches[0].calls.length : branches[b].at + 1 + branches[b].calls.length;
const owner = (b: number, i: number) => { while (b > 0 && i <= branches[b].at) b = branches[b].parent; return b; };
const callAt = (b: number, i: number) => { const o = owner(b, i); return branches[o].calls[o === 0 ? i : i - branches[o].at - 1]; };
const branchName = (b: number) => b === 0 ? 'Original' : `Branch ${b}`;
const forkLabel = (b: number) => branches[b].at < 0 ? 'from the start' : `after turn ${branches[b].at + 1} of ${branchName(branches[b].parent).toLowerCase()}`;
const MAX_CHANGE_ROWS = 500, MAX_WEB_ROWS = 500;
const pointValue = (p: Point) => `${p.i < 0 ? 0 : owner(p.b, p.i)}:${p.i}`;
const parsePoint = (v: string): Point => { const [b, i] = v.split(':').map(Number); return { b, i }; };

// ---------- loading ----------
let rec: LoadedRecording | null = null;
let cur: Point = { b: 0, i: -1 };
let selected: string | null = null;
const openDirs = new Set<string>();

async function openRecording(data: Uint8Array, sidecars: Record<string, Uint8Array>, name: string) {
  status(`Opening ${name}…`);
  try {
    const value = await request<LoadedRecording>({ t: 'load', data, sidecars });
    loaded = { data, sidecars, name };
    rec = value;
    branches = [{ parent: -1, at: -1, calls: rec.calls }];
    cur = { b: 0, i: -1 };
    live = null;
    liveEl.hidden = true;
    selected = null;
    openDirs.clear();
    renderSummary(name);
    renderBranches();
    renderCompareOptions();
    renderTimeline();
    viewer.hidden = false;
    status(`${name}: ${plural(rec.calls.length, 'turn')}.`);
    await go(rec.calls.length ? 0 : -1);
  } catch (e) {
    status(`Could not open ${name}: ${(e as Error).message}`, true);
  }
}

async function openFiles(files: File[]) {
  // a .topwater file, or an old .jsonl recording with the .zip snapshots it keeps beside it
  const recording = files.find(f => /\.(topwater|jsonl?)$/i.test(f.name)) ?? (files.length === 1 ? files[0] : files.find(f => !/\.zip$/i.test(f.name)));
  if (!recording) { status('Choose a .topwater file (or an old .jsonl recording and its .zip snapshots).', true); return; }
  const sidecars: Record<string, Uint8Array> = {};
  for (const f of files) if (f !== recording) sidecars[f.name] = new Uint8Array(await f.arrayBuffer());
  await openRecording(new Uint8Array(await recording.arrayBuffer()), sidecars, recording.name);
}

async function openSample() {
  try {
    const res = await fetch('samples/sales-report.jsonl');
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    await openRecording(new Uint8Array(await res.arrayBuffer()), {}, 'sales-report.jsonl (sample)');
  } catch (e) { status(`Could not load the sample: ${(e as Error).message}`, true); }
}

// ---------- rendering ----------
function renderSummary(name: string) {
  const r = rec!;
  $('rec-title').textContent = 'Recording';
  const meta = $('meta');
  meta.replaceChildren();
  const add = (k: string, v: string) => meta.append(el('dt', { textContent: k }), el('dd', { textContent: v }));
  if (r.metadata.task) add('Task', r.metadata.task);
  add('File', name);
  add('Turns', String(r.calls.length));
  for (const [k, v] of Object.entries(r.metadata)) if (k !== 'task') add(k.charAt(0).toUpperCase() + k.slice(1), v);
  add('Machine', `${r.user}@${r.hostname}`);
  add('Recorded by', r.topwater ? `Topwater ${r.topwater}` : 'Topwater');
  // whether a re-run turn matched is checked and shown per turn; this only says what the recording pins down
  const otherPort = r.topwater !== 'typescript';
  add('Deterministic', r.deterministic
    ? `virtual clock from ${fmtTime(r.startTime!)}, random seed ${r.randomSeed} (each re-run turn says whether it matched the recording)`
    : 'no: recorded on the real clock, so times and random numbers differ when turns are re-run');
  if (otherPort && r.calls.length)
    add('Re-run here', 'turns without a snapshot are re-run in this browser by Topwater\'s TypeScript port and compared with what it prints; it was recorded elsewhere, and the ports differ, so the output may too');
  if (r.appCommands.length) add('App commands', `${r.appCommands.join(', ')} (not available here: re-run turns that use them differ)`);
}

function renderTimeline() {
  timeline.replaceChildren();
  slider.max = String(len(cur.b));
  const items: [number, string, CallInfo | null][] = [[-1, 'Start: the machine before the first command', null]];
  for (let i = 0; i < len(cur.b); i++) items.push([i, callAt(cur.b, i).command, callAt(cur.b, i)]);
  for (const [index, command, call] of items) {
    const b = el('button', { type: 'button', cls: 'turn-btn' },
      el('span', { cls: 'n', textContent: index < 0 ? '0' : String(index + 1) }),
      el('span', { cls: 'cmd', textContent: command.split('\n')[0] + (command.includes('\n') ? ' …' : '') }));
    if (call && call.exitCode !== 0) b.append(el('span', { cls: 'exit', textContent: String(call.exitCode), title: `exit code ${call.exitCode}` }));
    if (call?.hasSnapshot) b.append(el('span', { cls: 'snap', textContent: '◆', title: 'the recording holds a snapshot after this turn' }));
    const mine = index >= 0 ? owner(cur.b, index) : 0;
    if (mine > 0) { b.classList.add('typed'); b.append(el('span', { cls: 'snap', textContent: `B${mine}`, title: `typed in ${branchName(mine).toLowerCase()}` })); }
    b.dataset.index = String(index);
    b.addEventListener('click', () => void go(index));
    timeline.append(el('li', {}, b));
  }
}

async function go(index: number) {
  if (!rec) return;
  const n = len(cur.b), b = cur.b;
  index = Math.max(-1, Math.min(n - 1, index));
  cur = { b, i: index };
  slider.value = String(index + 1);
  pos.textContent = (index < 0 ? `Start (of ${n} turns)` : `Turn ${index + 1} of ${n}`) + (b ? ` · ${branchName(b)}` : '');
  setCompare({ b, i: index - 1 }, cur);
  for (const b of timeline.querySelectorAll<HTMLButtonElement>('button')) {
    const on = Number(b.dataset.index) === index;
    if (on) { b.setAttribute('aria-current', 'step'); b.scrollIntoView({ block: 'nearest' }); } else b.removeAttribute('aria-current');
  }
  renderTurn(index, null);
  treeEl.setAttribute('aria-busy', 'true');
  try {
    const view = await request<TurnView>({ t: 'turn', p: cur });
    if (cur.i !== index || cur.b !== b) return; // the user moved on
    renderTurn(index, view);
    renderChanges(index < 0 ? null : view.changes, { b, i: index - 1 }, cur);
    renderTree(view);
    if (selected && !view.tree.some(e => e[0] === selected)) selected = null;
    selected ??= view.changes.find(c => !c.isDirectory && c.kind !== 'removed' && !/^\/(proc|usr|bin|etc|dev)\//.test(c.path))?.path ?? null;
    await showFile(selected);
  } catch (e) {
    if (cur.i === index && cur.b === b) status(`${turnLabel(index)}: ${(e as Error).message}`, true);
  } finally { treeEl.removeAttribute('aria-busy'); }
}

function renderTurn(index: number, view: TurnView | null) {
  const r = rec!;
  turnEl.replaceChildren();
  if (index < 0) {
    turnEl.append(el('h3', { id: 'turn-title', textContent: 'Start' }),
      el('p', { textContent: 'The machine as the recording began, before the agent ran anything. Step forward to see each command and what it did.' }));
    if (r.metadata.task) turnEl.append(el('blockquote', { textContent: r.metadata.task }));
    return;
  }
  const c = callAt(cur.b, index), mine = owner(cur.b, index);
  const h = el('h3', { id: 'turn-title', textContent: `Turn ${index + 1}` + (mine ? ` (typed in ${branchName(mine).toLowerCase()})` : '') });
  h.append(' ', el('span', { cls: c.exitCode === 0 ? 'badge ok' : 'badge bad', textContent: c.exitCode === 0 ? 'exit 0' : `exit ${c.exitCode}` }));
  const out = el('pre', { cls: 'out', tabIndex: 0, ariaLabel: 'Output' });
  if (c.stdout) out.append(el('span', { textContent: c.stdout }));
  if (c.stderr) out.append(el('span', { cls: 'err', textContent: c.stderr }));
  if (c.exitCode !== 0) out.append(el('span', { cls: 'dim', textContent: `${c.stdout || c.stderr ? (/\n$/.test(c.stdout + c.stderr) ? '' : '\n') : ''}[exit code ${c.exitCode}]\n` }));
  if (c.trailer) out.append(el('span', { cls: 'trailer', textContent: c.trailer + '\n' }));
  if (!c.stdout && !c.stderr && c.exitCode === 0 && !c.trailer) out.append(el('span', { cls: 'dim', textContent: '(no output)' }));
  const cmd = el('pre', { cls: 'cmd', tabIndex: 0, ariaLabel: 'Command' }, el('span', { cls: 'p', textContent: '$ ' }), c.command);
  const meta = el('p', { cls: 'note' }, `Took ${c.elapsedMs < 1 ? '<1' : Math.round(c.elapsedMs)} ms when ${mine ? 'typed' : 'recorded'}.`);
  if (view) meta.append(' ', sourceText(view));
  turnEl.append(h, cmd, out, meta);
  if (c.web?.length) turnEl.append(webList(c, { b: cur.b, i: index }));
  if (view?.source.kind === 'rerun' && !view.source.matches) {
    const s = view.source;
    const d = el('details', { cls: 'rerun' }, el('summary', { textContent: 'What re-running it here printed' }));
    const o = el('pre', { cls: 'out' });
    if (s.stdout) o.append(el('span', { textContent: s.stdout }));
    if (s.stderr) o.append(el('span', { cls: 'err', textContent: s.stderr }));
    o.append(el('span', { cls: 'dim', textContent: `[exit code ${s.exitCode}]` }));
    d.append(o);
    turnEl.append(d);
  }
}

// the requests a call made and what came back (recorded, so a re-run gets the same answers); all of it is untrusted text
function webList(c: CallInfo, p: Point): HTMLElement {
  const list = el('ul', { cls: 'web-list' });
  // like the Changes list: thousands of rows would block the page
  c.web!.slice(0, MAX_WEB_ROWS).forEach((x, n) => {
    const outcome = x.error ? `failed (${x.error})` : `${x.status}${x.reason ? ' ' + x.reason : ''}, ${fmtSize(x.size)}`;
    const li = el('li', {}, el('code', { textContent: `${x.method} ${x.url}` }), ' ', el('span', { cls: x.error || x.status >= 400 ? 'note error' : 'note', textContent: `→ ${outcome}` }));
    if (x.size > 0) {
      const d = el('details', { cls: 'diff' }, el('summary', { textContent: 'body', ariaLabel: `Body of ${x.method} ${x.url}` }));
      let filled = false;
      d.addEventListener('toggle', async () => {
        if (!d.open || filled) return;
        filled = true;
        try {
          const b = await request<WebBody>({ t: 'web', p, n });
          if (b.text === null) d.append(el('p', { cls: 'note', textContent: `Binary, ${fmtSize(b.size)}; not shown.` }));
          else {
            d.append(el('pre', { cls: 'content', tabIndex: 0, ariaLabel: `Body of ${x.method} ${x.url}`, textContent: b.text || '(empty)' }));
            if (b.truncated) d.append(el('p', { cls: 'note', textContent: `Showing the start of ${fmtSize(b.size)}.` }));
          }
        } catch (e) { d.append(el('p', { cls: 'note error', textContent: (e as Error).message })); }
      });
      li.append(d);
    }
    list.append(li);
  });
  const more = c.web!.length - MAX_WEB_ROWS;
  return el('section', { cls: 'web' }, el('h4', { textContent: `Web requests (${c.web!.length})` }), list,
    ...more > 0 ? [el('p', { cls: 'note', textContent: `…and ${more} more (showing the first ${MAX_WEB_ROWS}).` })] : []);
}

function sourceText(view: TurnView): string {
  const s = view.source;
  if (s.kind === 'snapshot') return 'The files below are the recording\'s own snapshot after this turn.';
  if (s.kind === 'start') return '';
  if (s.kind === 'typed') return 'The files below are the machine right after you ran it here.';
  const from = s.from < 0 ? 'the start' : `turn ${s.from + 1}`;
  return `The files below were rebuilt by re-running the turns after ${from} in this browser; ` +
    (s.matches ? 'this turn printed the same output as recorded.' : 'this turn printed something different from the recording (see below), so the files may differ too.');
}

const KIND_LABEL: Record<Change['kind'], string> = { added: 'added', removed: 'removed', modified: 'changed', replaced: 'replaced', permissions: 'permissions' };

// the two moments compared: by default the turn before the current one and the current one
function renderCompareOptions() {
  const keep = [cmpA.value, cmpZ.value];
  for (const sel of [cmpA, cmpZ]) {
    sel.replaceChildren();
    branches.forEach((br, b) => {
      const g = el('optgroup', { label: b === 0 ? 'Original' : `${branchName(b)} (${forkLabel(b)})` });
      g.dataset.b = String(b);
      for (let i = b === 0 ? -1 : br.at + 1; i < len(b); i++) g.append(compareOption(b, i));
      sel.append(g);
    });
  }
  [cmpA.value, cmpZ.value] = keep;
}
const compareOption = (b: number, i: number) =>
  el('option', { value: `${b}:${i}`, textContent: i < 0 ? 'Start' : `${b ? `B${b} ` : ''}turn ${i + 1}: ${callAt(b, i).command.split('\n')[0].slice(0, 60)}` });
/** a turn typed on branch `b`: one more option, not the whole list again */
function addCompareOption(b: number, i: number) {
  for (const sel of [cmpA, cmpZ]) sel.querySelector(`optgroup[data-b="${b}"]`)?.append(compareOption(b, i));
}

function setCompare(a: Point, z: Point) {
  cmpA.value = pointValue(a.i < -1 ? z : a);
  cmpZ.value = pointValue(z);
}

let compareToken = 0;
async function compareChosen() {
  const a = parsePoint(cmpA.value), z = parsePoint(cmpZ.value), token = ++compareToken;
  changesEl.setAttribute('aria-busy', 'true');
  try {
    const changes = await request<Change[]>({ t: 'compare', a, z });
    if (token === compareToken) renderChanges(changes, a, z);
  } catch (e) {
    if (token === compareToken) changesEl.replaceChildren(el('p', { cls: 'note error', textContent: (e as Error).message }));
  } finally { if (token === compareToken) changesEl.removeAttribute('aria-busy'); }
}

function renderChanges(changes: Change[] | null, a: Point, z: Point) {
  changesEl.replaceChildren();
  if (!changes) { changesEl.append(el('p', { cls: 'note', textContent: 'Nothing yet: this is the starting machine.' })); return; }
  if (changes.length === 0) { changesEl.append(el('p', { cls: 'note', textContent: 'No files differ.' })); return; }
  const list = el('ul', { cls: 'change-list' });
  let diffs = 0;
  // thousands of rows would block the page for seconds
  for (const c of changes.slice(0, MAX_CHANGE_ROWS)) {
    const li = el('li', { cls: `change ${c.kind}` });
    const badge = el('span', { cls: `kind ${c.kind}`, textContent: KIND_LABEL[c.kind] });
    const name = c.kind === 'removed'
      ? el('code', { textContent: c.path + (c.isDirectory ? '/' : '') })
      : el('button', { type: 'button', cls: 'linklike', textContent: c.path + (c.isDirectory ? '/' : '') });
    if (name instanceof HTMLButtonElement) name.addEventListener('click', () => { selected = c.path; revealInTree(c.path); void showFile(c.path); });
    li.append(badge, ' ', name);
    if (c.detail) li.append(' ', el('span', { cls: 'note', textContent: c.detail }));
    if (!c.isDirectory && !c.isLink && (c.kind === 'modified' || c.kind === 'added' || c.kind === 'removed')) {
      const d = el('details', { cls: 'diff' }, el('summary', { textContent: 'diff' }));
      let filled = false;
      const fill = async () => { if (filled) return; filled = true; d.append(await diffBlock(a, z, c.path)); };
      d.addEventListener('toggle', () => { if (d.open) void fill(); });
      if (diffs++ < 6 && !/^\/(proc|usr|bin)\//.test(c.path)) { d.open = true; void fill(); }
      li.append(d);
    }
    list.append(li);
  }
  changesEl.append(list);
  if (changes.length > MAX_CHANGE_ROWS) changesEl.append(el('p', { cls: 'note', textContent: `…and ${changes.length - MAX_CHANGE_ROWS} more (showing the first ${MAX_CHANGE_ROWS}; the agent's files come first).` }));
}

async function diffBlock(a: Point, z: Point, path: string): Promise<HTMLElement> {
  try {
    const d = await request<DiffView>({ t: 'diff', a, z, path });
    const side = (kind: string, size: number) => kind === 'missing' ? 'none' : `${kind}, ${fmtSize(size)}`;
    if (d.binary) return el('p', { cls: 'note', textContent: `Binary or very large file (${side(d.before, d.beforeSize)} → ${side(d.after, d.afterSize)}); no text diff.` });
    if (!d.hunks) return el('p', { cls: 'note', textContent: 'Too large to diff here.' });
    if (d.hunks.length === 0) return el('p', { cls: 'note', textContent: 'Same text (only its time or bytes elsewhere changed).' });
    const pre = el('pre', { cls: 'diff-body', tabIndex: 0, ariaLabel: `Changes to ${path}` });
    for (const h of d.hunks) {
      pre.append(el('span', { cls: 'hunk', textContent: `@@ -${h.oldStart},${h.oldLines} +${h.newStart},${h.newLines} @@\n` }));
      for (const l of h.lines) pre.append(el('span', { cls: l.op === '+' ? 'add' : l.op === '-' ? 'del' : 'ctx', textContent: `${l.op}${l.text}\n` }));
    }
    return pre;
  } catch (e) { return el('p', { cls: 'note error', textContent: (e as Error).message }); }
}

// the file tree: directories are <details> (keyboard and screen-reader friendly), files are buttons
let lastTree: TreeEntry[] = [];
let lastView: TurnView | null = null;
function renderTree(view: TurnView) {
  lastView = view;
  lastTree = view.tree;
  const changed = new Map(view.changes.map(c => [c.path, c.kind]));
  const children = new Map<string, TreeEntry[]>();
  for (const e of view.tree) {
    if (e[0] === '/') continue;
    const parent = e[0].slice(0, e[0].lastIndexOf('/')) || '/';
    (children.get(parent) ?? children.set(parent, []).get(parent)!).push(e);
  }
  // open by default: the way to the working directory and to every change
  const wanted = new Set<string>();
  const openTo = (p: string) => { for (let q = p; q && q !== '/'; q = q.slice(0, q.lastIndexOf('/'))) wanted.add(q); };
  openTo(view.cwd);
  for (const c of view.changes) if (!/^\/(proc|usr|bin|etc|dev)(\/|$)/.test(c.path)) openTo(c.path.slice(0, c.path.lastIndexOf('/')));
  if (selected) openTo(selected.slice(0, selected.lastIndexOf('/')));
  const build = (dir: string): HTMLElement => {
    const ul = el('ul', { role: 'group' });
    for (const e of (children.get(dir) ?? []).sort((a, b) => Number(b[1]) - Number(a[1]) || (a[0] < b[0] ? -1 : 1))) {
      const [path, isDir, mode, owner, , size] = e;
      const name = path.slice(path.lastIndexOf('/') + 1);
      const mark = changed.get(path);
      const tag = mark ? el('span', { cls: `kind ${mark}`, textContent: KIND_LABEL[mark] }) : '';
      if (isDir) {
        const d = el('details', {}, el('summary', { title: `${modeText(true, mode)} ${owner}` }, el('span', { cls: 'dir', textContent: name + '/' }), ' ', tag));
        d.dataset.path = path;
        d.open = openDirs.has(path) || wanted.has(path);
        let built = false;
        const fill = () => { if (!built) { built = true; d.append(build(path)); } };
        if (d.open) fill();
        d.addEventListener('toggle', () => { if (d.open) { openDirs.add(path); fill(); } else openDirs.delete(path); });
        ul.append(el('li', {}, d));
      } else {
        const b = el('button', { type: 'button', cls: 'file', title: `${modeText(false, mode)} ${owner} ${fmtSize(size)}` },
          el('span', { textContent: name }), ' ', tag);
        b.dataset.path = path;
        if (path === selected) b.setAttribute('aria-current', 'true');
        b.addEventListener('click', () => { selected = path; markSelected(); void showFile(path); });
        ul.append(el('li', {}, b));
      }
    }
    return ul;
  };
  const root = build('/');
  root.setAttribute('aria-label', 'Files');
  treeEl.replaceChildren(el('div', { cls: 'root', textContent: '/' }), root);
}

function markSelected() {
  for (const b of treeEl.querySelectorAll<HTMLButtonElement>('button.file'))
    if (b.dataset.path === selected) b.setAttribute('aria-current', 'true'); else b.removeAttribute('aria-current');
}

function revealInTree(path: string) {
  for (let q = path.slice(0, path.lastIndexOf('/')); q && q !== '/'; q = q.slice(0, q.lastIndexOf('/'))) openDirs.add(q);
  if (lastView) renderTree(lastView);
  treeEl.querySelector<HTMLElement>(`button.file[data-path="${CSS.escape(path)}"]`)?.scrollIntoView({ block: 'nearest' });
}

async function showFile(path: string | null) {
  const title = $('file-title');
  fileEl.replaceChildren();
  if (!path) { title.textContent = 'File'; fileEl.append(el('p', { cls: 'note', textContent: 'Choose a file in the tree.' })); return; }
  title.textContent = path;
  const entry = lastTree.find(e => e[0] === path);
  if (entry) fileEl.append(el('p', { cls: 'note', textContent: `${modeText(entry[1], entry[2])}  ${entry[3]}  ${fmtSize(entry[5])}  modified ${fmtTime(entry[4])}` }));
  try {
    const v = await request<FileView>({ t: 'file', p: cur, path });
    if (selected !== path) return;
    if (v.kind === 'text') {
      fileEl.append(el('pre', { cls: 'content', tabIndex: 0, ariaLabel: `Contents of ${path}`, textContent: v.text || '(empty file)' }));
      if (v.truncated) fileEl.append(el('p', { cls: 'note', textContent: 'Showing the first 256 KB.' }));
    } else if (v.kind === 'binary') fileEl.append(el('p', { cls: 'note', textContent: `Binary, ${fmtSize(v.size)}. First bytes:` }), el('pre', { cls: 'content', tabIndex: 0, textContent: hexdump(v.head) }));
    else if (v.kind === 'link') fileEl.append(el('p', { cls: 'note', textContent: `A symbolic link to ${v.target}` }));
    else fileEl.append(el('p', { cls: 'note', textContent: v.kind === 'directory' ? 'A directory.' : 'Not on the machine at this turn.' }));
  } catch (e) { fileEl.append(el('p', { cls: 'note error', textContent: (e as Error).message })); }
}

function hexdump(b: Uint8Array): string {
  let out = '';
  for (let i = 0; i < b.length; i += 16) {
    const row = b.subarray(i, i + 16);
    out += i.toString(16).padStart(8, '0') + '  ' + [...row].map(x => x.toString(16).padStart(2, '0')).join(' ').padEnd(48) + '  ' +
      [...row].map(x => x >= 32 && x < 127 ? String.fromCharCode(x) : '.').join('') + '\n';
  }
  return out;
}

// ---------- fork: the terminal demo on this turn's machine ----------
async function forkHere() {
  if (!rec || !loaded) return;
  try {
    const at = cur.i;
    const zip = await request<Uint8Array>({ t: 'fork', p: cur });
    let s = '';
    for (let i = 0; i < zip.length; i += 0x8000) s += String.fromCharCode(...zip.subarray(i, i + 0x8000));
    const key = forkKey();
    try { localStorage.setItem(FORK_PREFIX + key, btoa(s)); }
    catch { status('This machine is too large to hand to a new tab (browser storage is full); use "Continue from here" instead.', true); return; }
    const from = loaded.name + (cur.b ? `, ${branchName(cur.b).toLowerCase()}` : '');
    const url = `index.html?fork=${key}&turn=${at < 0 ? -1 : at + 1}&from=${encodeURIComponent(from)}`;
    const w = window.open(url, '_blank');
    if (!w) location.href = url; // a blocked pop-up: open it here
    else status(`Opened a terminal on the machine after ${turnLabel(at).toLowerCase()} in a new tab.`);
  } catch (e) { status(`Could not fork: ${(e as Error).message}`, true); }
}

// ---------- branches: the list, switching, downloading ----------
function renderBranches() {
  const hadFocus = branchesEl.contains(document.activeElement);
  branchesEl.replaceChildren();
  const add = (b: number, depth: number) => {
    const br = branches[b];
    const own = br.calls.length;
    const pick = el('button', { type: 'button', cls: 'branch-btn' },
      el('strong', { textContent: branchName(b) }), ' ',
      el('span', { cls: 'note', textContent: b === 0 ? plural(own, 'turn') : `${forkLabel(b)}, ${plural(own, 'new turn')}` }));
    if (b === cur.b) pick.setAttribute('aria-current', 'true');
    pick.addEventListener('click', () => switchTo(b));
    const li = el('li', { cls: 'branch' }, pick);
    li.style.marginLeft = `${Math.min(depth, 6) * 16}px`;
    if (b > 0) {
      const dl = el('button', { type: 'button', cls: 'secondary small', textContent: 'Download .topwater', ariaLabel: `Download ${branchName(b).toLowerCase()} as a .topwater file` });
      dl.addEventListener('click', () => void download(b));
      li.append(' ', dl);
    }
    branchesEl.append(li);
    branches.forEach((c, k) => { if (k > 0 && c.parent === b) add(k, depth + 1); });
  };
  add(0, 0);
  // the list was rebuilt: keep the keyboard on the chosen timeline
  if (hadFocus) branchesEl.querySelector<HTMLButtonElement>('.branch-btn[aria-current]')?.focus();
}

function switchTo(b: number, index = len(b) - 1) {
  cur = { b, i: cur.i };
  renderBranches();
  renderTimeline();
  void go(index);
}

async function download(b: number) {
  try {
    const name = loaded!.name.replace(/ \(sample\)$/, '').replace(/\.(topwater|jsonl?)$/i, '');
    status(`Saving ${branchName(b).toLowerCase()}…`);
    const data = await request<Uint8Array>({ t: 'export', b, from: loaded!.name }, [], 'saving this branch', 5 * REQUEST_TIMEOUT_MS);
    const url = URL.createObjectURL(new Blob([data as BlobPart], { type: 'application/zip' }));
    const a = el('a', { href: url, download: `${name}-branch-${b}.topwater` });
    document.body.append(a); a.click(); a.remove();
    setTimeout(() => URL.revokeObjectURL(url), 10_000);
    status(`Saved ${branchName(b).toLowerCase()} as ${a.download}: the recording's turns up to the fork, then the branch's.`);
  } catch (e) { status(`Could not save ${branchName(b).toLowerCase()}: ${(e as Error).message}`, true); }
}

// ---------- continue from here: a terminal whose commands become a branch ----------
/** where the next command goes: after call `i` of branch `b` (a new branch, unless that is the end of a branch) */
let live: Point | null = null;

function continueHere() {
  if (!rec) return;
  live = { ...cur };
  liveEl.hidden = false;
  $('live-title').textContent = `Continue from ${turnLabel(cur.i).toLowerCase()}${cur.b ? ` of ${branchName(cur.b).toLowerCase()}` : ''}`;
  liveOut.replaceChildren(el('span', { cls: 'dim', textContent: `This is the machine after ${turnLabel(cur.i).toLowerCase()}. ${cur.b && cur.i === len(cur.b) - 1 ? `Commands are added to ${branchName(cur.b).toLowerCase()}.` : 'Your commands start a new branch; the recording is left as it was.'}
` }));
  liveCmd.focus();
}

const RUNNING = 'Running the command; Stop ends it.';
async function runLive(command: string) {
  if (!live || !command.trim()) return;
  liveCmd.value = '';
  liveCmd.disabled = true;
  const block = el('span', {}, el('span', { cls: 'p', textContent: '$ ' }), command + '\n');
  liveOut.append(block);
  let b = live.b, made: Worker | null = null;
  try {
    await restoring;
    liveStop.hidden = false;
    status(RUNNING);
    if (b === 0 || live.i !== len(b) - 1) {
      b = await request<number>({ t: 'branch', parent: live.b, at: live.i });
      if (b !== branches.length) throw new Error('the viewer lost track of its branches; reload the recording');
      // filed under the timeline that holds the turn (the worker does the same)
      branches.push({ parent: live.i < 0 ? 0 : owner(live.b, live.i), at: live.i, calls: [] });
      made = worker;
      renderCompareOptions();
    }
    const c = await request<CallInfo>({ t: 'run', b, command }, [], 'running this command');
    branches[b].calls.push(c);
    addCompareOption(b, c.index);
    live = { b, i: c.index };
    if (c.stdout) block.append(el('span', { textContent: c.stdout }));
    if (c.stderr) block.append(el('span', { cls: 'err', textContent: c.stderr }));
    if (c.exitCode !== 0) block.append(el('span', { cls: 'dim', textContent: `[exit code ${c.exitCode}]\n` }));
    if (c.trailer) block.append(el('span', { cls: 'trailer', textContent: c.trailer + '\n' }));
    switchTo(b, c.index);
  } catch (e) {
    block.append(el('span', { cls: 'err', textContent: `${(e as Error).message}\n` }));
    // a branch this command started, on a worker since replaced: drop it before the new worker is given the branches
    if (made && made !== worker && b === branches.length - 1 && branches[b].calls.length === 0) { branches.pop(); renderCompareOptions(); }
  } finally {
    if (statusEl.textContent === RUNNING) status('');
    liveStop.hidden = true;
    liveCmd.disabled = false;
    liveOut.scrollTop = liveOut.scrollHeight;
    liveCmd.focus();
  }
}

// ---------- controls ----------
$('continue').addEventListener('click', continueHere);
$('live-form').addEventListener('submit', e => { e.preventDefault(); void runLive(liveCmd.value); });
// the 60 s limit stops a hung command anyway; this lets the user stop it sooner
liveStop.addEventListener('click', () => replaceWorker('stopped; nothing this command did was kept'));
$('live-close').addEventListener('click', () => { liveEl.hidden = true; live = null; $('continue').focus(); });
for (const sel of [cmpA, cmpZ]) sel.addEventListener('change', () => void compareChosen());
$('open').addEventListener('click', () => $<HTMLInputElement>('file').click());
$<HTMLInputElement>('file').addEventListener('change', e => { const input = e.target as HTMLInputElement; void openFiles([...input.files!]); input.value = ''; });
$('sample').addEventListener('click', () => void openSample());
$('first').addEventListener('click', () => void go(-1));
$('prev').addEventListener('click', () => void go(cur.i - 1));
$('next').addEventListener('click', () => void go(cur.i + 1));
$('last').addEventListener('click', () => void go(rec ? len(cur.b) - 1 : -1));
slider.addEventListener('input', () => void go(Number(slider.value) - 1));
$('fork').addEventListener('click', () => void forkHere());
document.addEventListener('keydown', e => {
  const t = e.target as HTMLElement;
  if (!rec || e.altKey || e.ctrlKey || e.metaKey || t.closest('input, textarea, select, summary') || (t instanceof HTMLButtonElement && (e.key === 'Enter' || e.key === ' '))) return;
  const move = { ArrowLeft: cur.i - 1, ArrowRight: cur.i + 1, Home: -1, End: len(cur.b) - 1 }[e.key];
  if (move === undefined) return;
  e.preventDefault();
  void go(move);
});
window.addEventListener('dragover', e => { if (e.dataTransfer?.types.includes('Files')) { e.preventDefault(); document.body.classList.add('dropping'); } });
window.addEventListener('dragleave', e => { if (!e.relatedTarget) document.body.classList.remove('dropping'); });
window.addEventListener('drop', e => {
  e.preventDefault();
  document.body.classList.remove('dropping');
  if (e.dataTransfer?.files.length) void openFiles([...e.dataTransfer.files]);
});

// ---------- theme: auto -> light -> dark (shared with the terminal page) ----------
const themeBtn = $('theme');
const themes = ['auto', 'light', 'dark'];
function applyTheme(t: string) {
  if (t === 'auto') document.documentElement.removeAttribute('data-theme'); else document.documentElement.dataset.theme = t;
  themeBtn.textContent = `Theme: ${t}`; themeBtn.dataset.theme = t;
}
try { applyTheme(localStorage.getItem('theme') ?? 'auto'); } catch { applyTheme('auto'); }
themeBtn.addEventListener('click', () => {
  const t = themes[(themes.indexOf(themeBtn.dataset.theme!) + 1) % 3];
  applyTheme(t);
  try { localStorage.setItem('theme', t); } catch { /* the choice just won't persist */ }
});

(window as unknown as { topwaterReplay: { go: typeof go; current: () => Point } }).topwaterReplay = { go, current: () => cur };
try { dropStaleForks(localStorage); } catch { /* storage unavailable: nothing was handed off either */ }
if (!new URLSearchParams(location.search).has('empty')) void openSample();
