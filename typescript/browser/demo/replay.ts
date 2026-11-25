// Recording viewer: steps through a Topwater recording turn by turn. The recording lives in a Web Worker
// (replay-worker.ts), which rebuilds the machine as of any turn; this page only renders what it answers.
import type { CallInfo, Change, DiffView, FileView, LoadedRecording, ReplayReply, ReplayRequest, TreeEntry, TurnView } from '../replay-protocol.js';

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const statusEl = $('status'), viewer = $('viewer'), slider = $<HTMLInputElement>('slider'), pos = $('pos');
const timeline = $('timeline'), turnEl = $('turn'), treeEl = $('tree'), fileEl = $('fileview'), changesEl = $('changes');
const REQUEST_TIMEOUT_MS = 60_000;

// ---------- the worker ----------
let worker: Worker;
let nextId = 1;
const pending = new Map<number, { resolve: (v: unknown) => void; reject: (e: Error) => void }>();
let loaded: { text: string; sidecars: Record<string, Uint8Array>; name: string } | null = null;

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
function request<T>(msg: Req, transfer: Transferable[] = []): Promise<T> {
  const id = nextId++;
  return new Promise<T>((resolve, reject) => {
    // re-running a recorded command can't be interrupted from inside; a stuck worker is replaced
    const timer = setTimeout(async () => {
      if (!pending.delete(id)) return;
      worker.terminate();
      for (const p of pending.values()) p.reject(new Error('cancelled'));
      pending.clear();
      startWorker();
      reject(new Error(`rebuilding this turn took longer than ${REQUEST_TIMEOUT_MS / 1000} s, so it was stopped`));
      if (loaded) await request({ t: 'load', text: loaded.text, sidecars: loaded.sidecars }).catch(() => undefined);
    }, REQUEST_TIMEOUT_MS);
    pending.set(id, {
      resolve: v => { clearTimeout(timer); resolve(v as T); },
      reject: e => { clearTimeout(timer); reject(e); },
    });
    worker.postMessage({ ...msg, id } as ReplayRequest, transfer);
  });
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

// ---------- loading ----------
let rec: LoadedRecording | null = null;
let current = -1;
let selected: string | null = null;
const openDirs = new Set<string>();

async function openRecording(text: string, sidecars: Record<string, Uint8Array>, name: string) {
  status(`Opening ${name}…`);
  try {
    const value = await request<LoadedRecording>({ t: 'load', text, sidecars });
    loaded = { text, sidecars, name };
    rec = value;
    selected = null;
    openDirs.clear();
    renderSummary(name);
    renderTimeline();
    viewer.hidden = false;
    status(`${name}: ${plural(rec.calls.length, 'turn')}.`);
    await go(rec.calls.length ? 0 : -1);
  } catch (e) {
    status(`Could not open ${name}: ${(e as Error).message}`, true);
  }
}

async function openFiles(files: File[]) {
  const recording = files.find(f => /\.jsonl?$/i.test(f.name)) ?? files.find(f => !/\.zip$/i.test(f.name));
  if (!recording) { status('Choose a .jsonl recording (and its .zip snapshots, if it keeps them separately).', true); return; }
  const sidecars: Record<string, Uint8Array> = {};
  for (const f of files) if (f !== recording) sidecars[f.name] = new Uint8Array(await f.arrayBuffer());
  await openRecording(await recording.text(), sidecars, recording.name);
}

async function openSample() {
  try {
    const res = await fetch('samples/sales-report.jsonl');
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    await openRecording(await res.text(), {}, 'sales-report.jsonl (sample)');
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
  add('Deterministic', r.deterministic
    ? `virtual clock from ${fmtTime(r.startTime!)}, random seed ${r.randomSeed} (each re-run turn says whether it matched the recording)`
    : 'no: recorded on the real clock, so times and random numbers differ when turns are re-run');
  if (r.appCommands.length) add('App commands', `${r.appCommands.join(', ')} (not available here: re-run turns that use them differ)`);
  slider.max = String(r.calls.length);
}

function renderTimeline() {
  timeline.replaceChildren();
  const items: [number, string, CallInfo | null][] = [[-1, 'Start: the machine before the first command', null], ...rec!.calls.map(c => [c.index, c.command, c] as [number, string, CallInfo])];
  for (const [index, command, call] of items) {
    const b = el('button', { type: 'button', cls: 'turn-btn' },
      el('span', { cls: 'n', textContent: index < 0 ? '0' : String(index + 1) }),
      el('span', { cls: 'cmd', textContent: command.split('\n')[0] + (command.includes('\n') ? ' …' : '') }));
    if (call && call.exitCode !== 0) b.append(el('span', { cls: 'exit', textContent: String(call.exitCode), title: `exit code ${call.exitCode}` }));
    if (call?.hasSnapshot) b.append(el('span', { cls: 'snap', textContent: '◆', title: 'the recording holds a snapshot after this turn' }));
    b.dataset.index = String(index);
    b.addEventListener('click', () => void go(index));
    timeline.append(el('li', {}, b));
  }
}

async function go(index: number) {
  if (!rec) return;
  index = Math.max(-1, Math.min(rec.calls.length - 1, index));
  current = index;
  slider.value = String(index + 1);
  pos.textContent = index < 0 ? `Start (of ${rec.calls.length} turns)` : `Turn ${index + 1} of ${rec.calls.length}`;
  for (const b of timeline.querySelectorAll<HTMLButtonElement>('button')) {
    const on = Number(b.dataset.index) === index;
    if (on) { b.setAttribute('aria-current', 'step'); b.scrollIntoView({ block: 'nearest' }); } else b.removeAttribute('aria-current');
  }
  renderTurn(index, null);
  treeEl.setAttribute('aria-busy', 'true');
  try {
    const view = await request<TurnView>({ t: 'turn', index });
    if (current !== index) return; // the user moved on
    renderTurn(index, view);
    renderChanges(view);
    renderTree(view);
    if (selected && !view.tree.some(e => e[0] === selected)) selected = null;
    selected ??= view.changes.find(c => !c.isDirectory && c.kind !== 'removed' && !/^\/(proc|usr|bin|etc|dev)\//.test(c.path))?.path ?? null;
    await showFile(selected);
  } catch (e) {
    if (current === index) status(`${turnLabel(index)}: ${(e as Error).message}`, true);
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
  const c = r.calls[index];
  const h = el('h3', { id: 'turn-title', textContent: `Turn ${index + 1}` });
  h.append(' ', el('span', { cls: c.exitCode === 0 ? 'badge ok' : 'badge bad', textContent: c.exitCode === 0 ? 'exit 0' : `exit ${c.exitCode}` }));
  const out = el('pre', { cls: 'out', tabIndex: 0, ariaLabel: 'Output' });
  if (c.stdout) out.append(el('span', { textContent: c.stdout }));
  if (c.stderr) out.append(el('span', { cls: 'err', textContent: c.stderr }));
  if (c.exitCode !== 0) out.append(el('span', { cls: 'dim', textContent: `${c.stdout || c.stderr ? (/\n$/.test(c.stdout + c.stderr) ? '' : '\n') : ''}[exit code ${c.exitCode}]\n` }));
  if (c.trailer) out.append(el('span', { cls: 'trailer', textContent: c.trailer + '\n' }));
  if (!c.stdout && !c.stderr && c.exitCode === 0 && !c.trailer) out.append(el('span', { cls: 'dim', textContent: '(no output)' }));
  const cmd = el('pre', { cls: 'cmd', tabIndex: 0, ariaLabel: 'Command' }, el('span', { cls: 'p', textContent: '$ ' }), c.command);
  const meta = el('p', { cls: 'note' }, `Took ${c.elapsedMs < 1 ? '<1' : Math.round(c.elapsedMs)} ms when recorded.`);
  if (view) meta.append(' ', sourceText(view));
  turnEl.append(h, cmd, out, meta);
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

function sourceText(view: TurnView): string {
  const s = view.source;
  if (s.kind === 'snapshot') return 'The files below are the recording\'s own snapshot after this turn.';
  if (s.kind === 'start') return '';
  const from = s.from < 0 ? 'the start' : `turn ${s.from + 1}`;
  return `The files below were rebuilt by re-running the turns after ${from} in this browser; ` +
    (s.matches ? 'this turn printed the same output as recorded.' : 'this turn printed something different from the recording (see below), so the files may differ too.');
}

const KIND_LABEL: Record<Change['kind'], string> = { added: 'added', removed: 'removed', modified: 'changed', replaced: 'replaced', permissions: 'permissions' };

function renderChanges(view: TurnView) {
  changesEl.replaceChildren();
  if (view.index < 0) { changesEl.append(el('p', { cls: 'note', textContent: 'Nothing yet: this is the starting machine.' })); return; }
  if (view.changes.length === 0) { changesEl.append(el('p', { cls: 'note', textContent: 'No files changed in this turn.' })); return; }
  const list = el('ul', { cls: 'change-list' });
  let diffs = 0;
  for (const c of view.changes) {
    const li = el('li', { cls: `change ${c.kind}` });
    const badge = el('span', { cls: `kind ${c.kind}`, textContent: KIND_LABEL[c.kind] });
    const name = c.kind === 'removed'
      ? el('code', { textContent: c.path + (c.isDirectory ? '/' : '') })
      : el('button', { type: 'button', cls: 'linklike', textContent: c.path + (c.isDirectory ? '/' : '') });
    if (name instanceof HTMLButtonElement) name.addEventListener('click', () => { selected = c.path; revealInTree(c.path); void showFile(c.path); });
    li.append(badge, ' ', name);
    if (c.detail) li.append(' ', el('span', { cls: 'note', textContent: c.detail }));
    if (!c.isDirectory && (c.kind === 'modified' || c.kind === 'added' || c.kind === 'removed')) {
      const d = el('details', { cls: 'diff' }, el('summary', { textContent: 'diff' }));
      let filled = false;
      const fill = async () => { if (filled) return; filled = true; d.append(await diffBlock(view.index, c.path)); };
      d.addEventListener('toggle', () => { if (d.open) void fill(); });
      if (diffs++ < 6 && !/^\/(proc|usr|bin)\//.test(c.path)) { d.open = true; void fill(); }
      li.append(d);
    }
    list.append(li);
  }
  changesEl.append(list);
}

async function diffBlock(index: number, path: string): Promise<HTMLElement> {
  try {
    const d = await request<DiffView>({ t: 'diff', index, path });
    if (d.binary) return el('p', { cls: 'note', textContent: `Binary or very large file (${d.before} → ${d.after}); no text diff.` });
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
    const v = await request<FileView>({ t: 'file', index: current, path });
    if (selected !== path) return;
    if (v.kind === 'text') {
      fileEl.append(el('pre', { cls: 'content', tabIndex: 0, ariaLabel: `Contents of ${path}`, textContent: v.text || '(empty file)' }));
      if (v.truncated) fileEl.append(el('p', { cls: 'note', textContent: 'Showing the first 256 KB.' }));
    } else if (v.kind === 'binary') fileEl.append(el('p', { cls: 'note', textContent: `Binary, ${fmtSize(v.size)}. First bytes:` }), el('pre', { cls: 'content', tabIndex: 0, textContent: hexdump(v.head) }));
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
    const zip = await request<Uint8Array>({ t: 'fork', index: current });
    let s = '';
    for (let i = 0; i < zip.length; i += 0x8000) s += String.fromCharCode(...zip.subarray(i, i + 0x8000));
    const key = Math.random().toString(36).slice(2, 12) + Date.now().toString(36);
    try { localStorage.setItem('topwater-fork:' + key, btoa(s)); }
    catch { status('This machine is too large to hand to the terminal page (browser storage is full).', true); return; }
    const url = `index.html?fork=${key}&turn=${current < 0 ? -1 : current + 1}&from=${encodeURIComponent(loaded.name)}`;
    const w = window.open(url, '_blank');
    if (!w) location.href = url; // a blocked pop-up: open it here
    else status(`Opened a terminal on the machine after ${turnLabel(current).toLowerCase()} in a new tab.`);
  } catch (e) { status(`Could not fork: ${(e as Error).message}`, true); }
}

// ---------- controls ----------
$('open').addEventListener('click', () => $<HTMLInputElement>('file').click());
$<HTMLInputElement>('file').addEventListener('change', e => { const input = e.target as HTMLInputElement; void openFiles([...input.files!]); input.value = ''; });
$('sample').addEventListener('click', () => void openSample());
$('first').addEventListener('click', () => void go(-1));
$('prev').addEventListener('click', () => void go(current - 1));
$('next').addEventListener('click', () => void go(current + 1));
$('last').addEventListener('click', () => void go(rec ? rec.calls.length - 1 : -1));
slider.addEventListener('input', () => void go(Number(slider.value) - 1));
$('fork').addEventListener('click', () => void forkHere());
document.addEventListener('keydown', e => {
  const t = e.target as HTMLElement;
  if (!rec || e.altKey || e.ctrlKey || e.metaKey || t.closest('input, textarea, select, summary') || (t instanceof HTMLButtonElement && (e.key === 'Enter' || e.key === ' '))) return;
  const move = { ArrowLeft: current - 1, ArrowRight: current + 1, Home: -1, End: rec.calls.length - 1 }[e.key];
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

(window as unknown as { topwaterReplay: { go: typeof go; current: () => number } }).topwaterReplay = { go, current: () => current };
if (!new URLSearchParams(location.search).has('empty')) void openSample();
