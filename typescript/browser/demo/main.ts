// Demo page: a terminal UI over TopwaterSession (the shell runs in a Web Worker).
import { TopwaterSession } from '../session.js';

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const term = $('term'), out = $('out'), input = $<HTMLTextAreaElement>('cmd'), promptEl = $('prompt'), drop = term.querySelector<HTMLElement>('.drop')!;

const q = new URLSearchParams(location.search);
const limitS = Number(q.get('limit')) || 10;

const seed: [string, string][] = [
  ['/home/agent/notes.txt', `Welcome to Topwater.

This is a simulated Linux shell running in your browser tab. Try:
  - the buttons on the right (grep, awk, jq, tar ...)
  - ls, cd, cat, pipes, redirects, for loops, functions
  - Upload files with the button (or drop them on the terminal); they land in /home/agent
  - Run "download FILE" to save any file to your computer
`],
  ['/home/agent/logs/app.log', `2025-03-01 09:00:01 INFO  server started on :8080
2025-03-01 09:00:05 INFO  connected to database
2025-03-01 09:01:12 WARN  slow query (1840 ms): SELECT * FROM orders
2025-03-01 09:02:30 INFO  GET /api/orders 200
2025-03-01 09:03:44 ERROR payment gateway timeout (order 1003)
2025-03-01 09:03:45 INFO  retrying payment (order 1003)
2025-03-01 09:04:02 INFO  GET /api/orders/1003 200
2025-03-01 09:05:18 WARN  cache miss rate 41%
2025-03-01 09:06:50 ERROR failed to send email to customer 77: connection refused
2025-03-01 09:07:01 INFO  GET /health 200
2025-03-01 09:08:33 INFO  POST /api/orders 201
2025-03-01 09:09:10 ERROR unhandled exception in worker 3: null reference
`],
  ['/home/agent/data/sales.csv', `region,rep,amount
north,alice,1200
north,bob,800
south,carol,950
south,dave,1430
east,erin,700
east,frank,1100
west,grace,1650
west,heidi,420
`],
  ['/home/agent/data/orders.json', JSON.stringify([
    { id: 1001, status: 'open', total: 59.9, customer: 'Ada' },
    { id: 1002, status: 'shipped', total: 120, customer: 'Grace' },
    { id: 1003, status: 'open', total: 15.25, customer: 'Linus' },
    { id: 1004, status: 'cancelled', total: 8, customer: 'Ken' },
    { id: 1005, status: 'open', total: 230.1, customer: 'Barbara' },
  ], null, 2) + '\n'],
];

const examples = [
  'help', 'ls -la', 'cat notes.txt', 'grep -n ERROR logs/app.log',
  "awk -F, 'NR>1 {s+=$3} END {print s}' data/sales.csv",
  `jq '.[] | select(.status=="open") | .id' data/orders.json`,
  'tar czf backup.tgz data logs && tar tzf backup.tgz',
  'for n in 8 16 32 64; do echo "2^$n = $((2**n))"; done',
  'cat /etc/os-release',
];
const DOWNLOAD_HOME = 'tar czf /tmp/home.tgz -C /home agent && download /tmp/home.tgz';

function save(name: string, data: Uint8Array) {
  const url = URL.createObjectURL(new Blob([data as BlobPart]));
  const a = document.createElement('a');
  a.href = url; a.download = name.split('/').pop() || 'download';
  document.body.append(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 10_000);
}

// ?fork=KEY: the recording viewer left a machine (snapshot zip, base64) in localStorage; this tab keeps it for reloads
function forkedMachine(): Uint8Array | undefined {
  const key = q.get('fork');
  if (!key || !/^[a-z0-9]{1,40}$/.test(key)) return undefined;
  try {
    const name = 'topwater-fork:' + key;
    const kept = sessionStorage.getItem(name), b64 = kept ?? localStorage.getItem(name);
    if (b64 === null) return undefined;
    const s = atob(b64), bytes = new Uint8Array(s.length);
    for (let i = 0; i < s.length; i++) bytes[i] = s.charCodeAt(i);
    // the hand-off copy goes only once this tab holds its own; if that fails it stays, so a reload still finds the machine
    if (kept === null) try { sessionStorage.setItem(name, b64); localStorage.removeItem(name); } catch { /* kept in localStorage */ }
    return bytes;
  } catch { return undefined; }
}
const fork = forkedMachine();

const session = new TopwaterSession({
  workerUrl: new URL('worker.js', import.meta.url),
  shell: { maxCallDurationMs: limitS * 1000 },
  graceMs: q.has('grace') ? Number(q.get('grace')) : 1500,
  seed: fork ? undefined : seed,
  snapshotZip: fork,
  onDownload: save,
});
(window as unknown as { topwater: TopwaterSession }).topwater = session;

// ---- output helpers (always textContent: output is untrusted) ----
function span(text: string, cls = '', parent: HTMLElement = out) {
  const s = document.createElement('span');
  s.textContent = text; if (cls) s.className = cls;
  parent.append(s); return s;
}
const toBottom = () => { term.scrollTop = term.scrollHeight; };
function line(text: string, cls = '') { span(text + '\n', cls); toBottom(); }

function promptText() {
  const cwd = session.cwd === session.home ? '~' : session.cwd.startsWith(session.home + '/') ? '~' + session.cwd.slice(session.home.length) : session.cwd;
  return `${session.user}@${session.hostname}:${cwd}${session.user === 'root' ? '#' : '$'}`;
}
const refreshPrompt = () => { promptEl.textContent = promptText(); };

// ---- running commands ----
const history: string[] = [];
let hIdx = 0, draft = '', busy = false;
const setBusy = (b: boolean) => { busy = b; term.dataset.busy = String(b); };

function growInput() { input.rows = Math.min(10, input.value.split('\n').length); }

async function runCommand(cmd: string) {
  if (busy || !cmd.trim()) return;
  if (history[history.length - 1] !== cmd) history.push(cmd);
  hIdx = history.length; draft = ''; input.value = ''; growInput();
  if (cmd.trim() === 'clear') { out.textContent = ''; return; }
  setBusy(true);
  const block = document.createElement('div');
  out.append(block);
  span(promptText() + ' ', 'p', block); span(cmd + '\n', '', block);
  const body = span('', '', block);
  toBottom();
  try {
    const r = await session.run(cmd, (text, stream) => { span(text, stream === 'stderr' ? 'err' : '', body); toBottom(); });
    body.textContent = ''; // the stream was a preview; the result is authoritative
    if (r.stdout) span(r.stdout, '', body);
    if (r.stderr) span(r.stderr, 'err', body);
    for (const [text, cls] of [[r.exitCode !== 0 && `[exit code ${r.exitCode}]`, 'dim'], [r.trailer, 'trailer']] as const)
      if (text) span(text + '\n', cls, block);
  } catch (e) {
    span(`error: ${(e as Error).message}\n`, 'err', block);
  }
  refreshPrompt(); setBusy(false); toBottom(); input.focus();
}

// ---- input ----
input.addEventListener('input', growInput);
input.addEventListener('keydown', e => {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) { e.preventDefault(); void runCommand(input.value); }
  else if (e.key === 'l' && e.ctrlKey) { e.preventDefault(); out.textContent = ''; }
  else if ((e.key === 'ArrowUp' || e.key === 'ArrowDown') && !e.shiftKey && !e.altKey && !e.ctrlKey) {
    const multi = input.value.includes('\n'); // let the caret move inside a multi-line entry
    if (multi && (e.key === 'ArrowUp' ? input.selectionStart > input.value.indexOf('\n') : input.selectionEnd <= input.value.lastIndexOf('\n'))) return;
    e.preventDefault();
    if (hIdx === history.length) draft = input.value;
    hIdx = Math.max(0, Math.min(history.length, hIdx + (e.key === 'ArrowUp' ? -1 : 1)));
    input.value = hIdx === history.length ? draft : history[hIdx]; growInput();
    input.setSelectionRange(input.value.length, input.value.length);
  }
});
term.addEventListener('click', () => { if (!getSelection()?.toString()) input.focus(); });

// ---- try these ----
const list = $('examples');
for (const cmd of examples) {
  const b = document.createElement('button');
  b.type = 'button'; b.textContent = cmd;
  b.addEventListener('click', () => { input.value = cmd; void runCommand(cmd); });
  const li = document.createElement('li'); li.append(b); list.append(li);
}

// ---- files ----
async function upload(files: Iterable<File>) {
  for (const f of files) {
    try {
      const path = await session.put(f.name, new Uint8Array(await f.arrayBuffer()));
      line(`uploaded: ${f.name} → ${path} (${f.size.toLocaleString('en-US').replace(/,/g, ' ')} bytes)`, 'dim');
    } catch (e) { line(`upload failed: ${f.name}: ${(e as Error).message}`, 'err'); }
  }
  refreshPrompt();
}
const picker = $<HTMLInputElement>('file');
$('upload').addEventListener('click', () => picker.click());
picker.addEventListener('change', () => { void upload([...picker.files!]); picker.value = ''; });
$('download').addEventListener('click', () => { input.value = DOWNLOAD_HOME; void runCommand(DOWNLOAD_HOME); });

let dragDepth = 0;
const isFiles = (e: DragEvent) => e.dataTransfer?.types.includes('Files');
const showDrop = (on: boolean) => { drop.hidden = !on; term.classList.toggle('over', on); };
term.addEventListener('dragenter', e => { if (isFiles(e)) { e.preventDefault(); dragDepth++; showDrop(true); } });
term.addEventListener('dragover', e => { if (isFiles(e)) e.preventDefault(); });
term.addEventListener('dragleave', () => { if (--dragDepth <= 0) { dragDepth = 0; showDrop(false); } });
term.addEventListener('drop', e => { e.preventDefault(); dragDepth = 0; showDrop(false); if (e.dataTransfer?.files.length) void upload([...e.dataTransfer.files]); });
// a file dropped outside the terminal must not navigate away from the page
for (const t of ['dragover', 'drop'] as const) window.addEventListener(t, e => { if (!term.contains(e.target as Node)) e.preventDefault(); });

// ---- theme: auto -> light -> dark ----
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
  try { localStorage.setItem('theme', t); } catch { /* storage unavailable: the choice just won't persist */ }
});

// ---- start ----
setBusy(true);
line('Topwater (TypeScript port): simulated bash, running in this tab. Type `help`, or try a command on the right.', 'dim');
if (fork) line(`Forked from ${q.get('from') || 'a recording'}, ${q.get('turn') === '-1' ? 'before the first turn' : `after turn ${q.get('turn') ?? '?'}`}: this is that machine, and it's yours to change.`, 'dim');
else if (q.has('fork')) line('The forked machine is no longer available here. Open the fork again from the recording viewer.', 'err');
session.start().then(() => { refreshPrompt(); setBusy(false); input.focus(); }, e => line(`could not start the shell: ${(e as Error).message}`, 'err'));
