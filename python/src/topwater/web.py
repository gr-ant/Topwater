"""A small web terminal for poking at a Topwater shell by hand.

    python -m topwater.web [--demo] [--port 8000] [--host 127.0.0.1]

Serves one page with a terminal; every command runs in a single in-memory session, exactly as an agent's tool call
would. Standard library only. It listens on localhost by default; there is no authentication.
"""
from __future__ import annotations

import argparse
import asyncio
import json
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Annotated
from urllib.parse import urlsplit

from . import Shell, command

MAX_BODY = 1_000_000

ICON = ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 100 100"><rect width="100" height="100" rx="23" fill="#0B5394"/>'
        '<g transform="translate(50,50) scale(1.3) translate(-47,-50)"><path d="M24,39 L35,50 L24,61" fill="none" stroke="#fff" '
        'stroke-width="8" stroke-linecap="round" stroke-linejoin="round"/><circle cx="57" cy="50" r="17" fill="#fff"/>'
        '<circle cx="63" cy="46.5" r="3.3" fill="#0B5394"/></g></svg>')


# ---------- demo session ----------

_ORDERS = [
    {"id": 101, "customer": "Acme Corp", "status": "open", "total": 250.0},
    {"id": 102, "customer": "Globex", "status": "shipped", "total": 80.0},
    {"id": 103, "customer": "Initech", "status": "open", "total": 45.5},
    {"id": 104, "customer": "Umbrella", "status": "open", "total": 199.99},
]


class _DemoTools:
    @command("orders", description="Query the order database (demo data)")
    def orders(self, status: Annotated[str | None, "Filter by status (open|shipped)"] = None,
               min_total: Annotated[float, "Only orders at or above this total"] = 0.0):
        return [o for o in _ORDERS if (status is None or o["status"] == status) and o["total"] >= min_total]


def make_demo_shell() -> Shell:
    sh = Shell().register(_DemoTools())
    fs = sh.fs
    fs.mkdir("/home/agent/project/src", parents=True)
    fs.write_text("/home/agent/project/src/app.py", "import os\n\n# TODO: read config from env\ndef main():\n    print('hi')  # TODO: real output\n")
    fs.write_text("/home/agent/project/src/util.py", "def add(a, b):\n    return a + b\n\n# TODO: add tests\n")
    fs.write_text("/home/agent/project/README.md", "# Demo project\n\nRun `python src/app.py`.\n")
    fs.mkdir("/home/agent/data", parents=True)
    fs.write_text("/home/agent/data/sales.csv", "date,region,amount\n2026-01-03,north,120.50\n2026-01-04,south,80\n"
                                                "2026-01-04,north,99.50\n2026-01-05,east,42\n2026-01-06,south,20\n")
    fs.write_text("/var/log/app.log", "".join(
        f"2026-11-2{i % 4} 10:0{i % 10}:00 {'ERROR' if i % 5 == 0 else 'INFO'} request {i}\n" for i in range(40)))
    return sh


# ---------- session ----------

class _Session:
    def __init__(self, factory):
        self.factory = factory
        self.lock = threading.Lock()
        self.shell = factory()

    def state(self) -> dict:
        sh = self.shell
        return {"user": sh.user, "host": sh.hostname, "cwd": sh.cwd, "home": sh.env.get("HOME", "")}

    def run(self, cmd: str) -> dict:
        with self.lock:
            r = asyncio.run(self.shell.run_async(cmd))
            return {"stdout": r.stdout, "stderr": r.stderr, "exit_code": r.exit_code, **self.state()}

    def reset(self) -> dict:
        with self.lock:
            self.shell = self.factory()
            return self.state()

    def complete(self, line: str) -> list[str]:
        """Completions for the last word: commands in first position, otherwise paths."""
        with self.lock:
            sh = self.shell
            words = line.split(" ")
            word = words[-1]
            if len(words) == 1:
                names = set(sh.commands) | set(sh.functions) | set(sh.aliases)
                return sorted(n for n in names if n.startswith(word))[:50]
            base, _, partial = word.rpartition("/")
            expanded = sh.env.get("HOME", "/") + base[1:] if base.startswith("~") else base
            directory = sh.resolve(expanded or ("/" if word.startswith("/") else "."))
            prefix = base + "/" if base or word.startswith("/") else ""
            previous, sh.fs.run_as = sh.fs.run_as, sh.user  # only show what the agent could see
            try:
                if not sh.fs.is_dir(directory) or not sh.fs.can_read(directory):
                    return []
                out = []
                for n in sh.fs.listdir(directory):
                    if n.startswith(partial) and (partial.startswith(".") or not n.startswith(".")):
                        out.append(prefix + n + ("/" if sh.fs.is_dir(f"{directory.rstrip('/')}/{n}") else ""))
                return out[:50]
            finally:
                sh.fs.run_as = previous


# ---------- HTTP ----------

def _handler(session: _Session):
    class Handler(BaseHTTPRequestHandler):
        server_version = "Topwater"

        def log_message(self, fmt, *args):  # keep the console quiet
            pass

        def _send(self, status: int, body: bytes, ctype: str) -> None:
            self.send_response(status)
            self.send_header("Content-Type", ctype)
            self.send_header("Content-Length", str(len(body)))
            self.send_header("Cache-Control", "no-store")
            self.end_headers()
            self.wfile.write(body)

        def _json(self, data, status: int = 200) -> None:
            self._send(status, json.dumps(data).encode(), "application/json")

        def do_GET(self):
            path = urlsplit(self.path).path
            if path in ("/", "/index.html"):
                self._send(200, PAGE.encode(), "text/html; charset=utf-8")
            elif path == "/favicon.svg":
                self._send(200, ICON.encode(), "image/svg+xml")
            elif path == "/api/state":
                self._json(session.state())
            else:
                self._json({"error": "not found"}, 404)

        def do_POST(self):
            length = int(self.headers.get("Content-Length") or 0)
            if length > MAX_BODY:
                return self._json({"error": "request too large"}, 413)
            try:
                body = json.loads(self.rfile.read(length) or b"{}")
            except ValueError:
                return self._json({"error": "invalid JSON"}, 400)
            path = urlsplit(self.path).path
            if path == "/api/run":
                cmd = body.get("command")
                if not isinstance(cmd, str):
                    return self._json({"error": "command must be a string"}, 400)
                self._json(session.run(cmd))
            elif path == "/api/reset":
                self._json(session.reset())
            elif path == "/api/complete":
                self._json({"completions": session.complete(str(body.get("line", "")))})
            else:
                self._json({"error": "not found"}, 404)

    return Handler


def serve(host: str = "127.0.0.1", port: int = 8000, demo: bool = False, factory=None) -> None:
    """Starts the web terminal. Pass `factory` to serve your own pre-configured Shell."""
    session = _Session(factory or (make_demo_shell if demo else Shell))
    httpd = ThreadingHTTPServer((host, port), _handler(session))
    print(f"Topwater web terminal on http://{host}:{port}  ({'demo session' if demo else 'empty session'}, Ctrl+C to stop)")
    if host not in ("127.0.0.1", "localhost", "::1"):
        print("warning: listening beyond localhost with no authentication")
    try:
        httpd.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        httpd.server_close()


def main() -> None:
    p = argparse.ArgumentParser(prog="python -m topwater.web", description="Web terminal for a Topwater shell")
    p.add_argument("--host", default="127.0.0.1")
    p.add_argument("--port", type=int, default=8000)
    p.add_argument("--demo", action="store_true", help="preload sample files and an `orders` command")
    a = p.parse_args()
    serve(a.host, a.port, a.demo)


PAGE = r"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Topwater Terminal</title>
<link rel="icon" href="/favicon.svg" type="image/svg+xml">
<style>
  :root {
    --brand: #0B5394; --brand-2: #0d63b0;
    --bg: #0b0f14; --panel: #0f151c; --line: #1c2733;
    --text: #d7e0ea; --muted: #7d8da0; --err: #ff8b8b; --ok: #7ee2a8; --path: #7fb6ee;
    --mono: ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace;
  }
  * { box-sizing: border-box; }
  html, body { height: 100%; margin: 0; background: var(--bg); color: var(--text); }
  body { display: flex; flex-direction: column; font: 14px/1.45 system-ui, -apple-system, "Segoe UI", sans-serif; }
  header {
    display: flex; align-items: center; gap: 12px; padding: 10px 16px;
    background: var(--brand); color: #fff; flex-wrap: wrap;
  }
  header img { width: 28px; height: 28px; border-radius: 7px; box-shadow: 0 0 0 2px rgba(255,255,255,.25); }
  header h1 { font-size: 17px; margin: 0; font-weight: 650; letter-spacing: .2px; }
  header .who { font: 12.5px var(--mono); opacity: .85; }
  header .spacer { flex: 1; }
  button {
    font: inherit; font-size: 13px; color: #fff; background: rgba(255,255,255,.12);
    border: 1px solid rgba(255,255,255,.28); border-radius: 7px; padding: 5px 11px; cursor: pointer;
  }
  button:hover { background: rgba(255,255,255,.22); }
  button:focus-visible, .chip:focus-visible { outline: 2px solid #fff; outline-offset: 2px; }
  main { flex: 1; overflow-y: auto; padding: 14px 16px 6px; font: 13.5px/1.5 var(--mono); }
  .intro { color: var(--muted); margin-bottom: 12px; font-family: system-ui, sans-serif; }
  .chips { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; }
  .chip {
    text-align: left; font: 12.5px var(--mono); color: var(--text); background: var(--panel); border: 1px solid var(--line);
    border-radius: 999px; padding: 3px 10px; cursor: pointer;
  }
  .chip:hover { border-color: var(--brand-2); }
  .entry { margin-bottom: 10px; }
  .prompt { color: var(--ok); white-space: pre-wrap; word-break: break-word; }
  .prompt .path { color: var(--path); }
  .prompt .cmd { color: var(--text); }
  pre { margin: 0; white-space: pre-wrap; word-break: break-word; font: inherit; }
  pre.err { color: var(--err); }
  .code { display: inline-block; margin-top: 2px; font-size: 11.5px; color: var(--err); border: 1px solid #5a2a2a;
          border-radius: 5px; padding: 0 6px; }
  .completions { color: var(--muted); }
  form { display: flex; align-items: flex-start; gap: 8px; padding: 10px 16px 14px; border-top: 1px solid var(--line);
         background: var(--panel); font: 13.5px var(--mono); }
  form .prompt { padding-top: 2px; flex: none; max-width: 45%; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
  textarea {
    flex: 1; min-width: 0; resize: none; border: 0; outline: 0; background: transparent; color: var(--text);
    font: inherit; padding: 2px 0; height: 1.5em; caret-color: var(--ok);
  }
  .busy textarea { opacity: .5; }
  .hint { color: var(--muted); font: 11.5px system-ui, sans-serif; padding: 0 16px 8px; background: var(--panel); }
  @media (max-width: 600px) { header .who, .uh { display: none; } form .prompt { max-width: 45%; } }
</style>
</head>
<body>
<header>
  <img src="/favicon.svg" alt="">
  <h1>Topwater</h1>
  <span class="who" id="who"></span>
  <span class="spacer"></span>
  <button type="button" id="help">help</button>
  <button type="button" id="clear">Clear</button>
  <button type="button" id="reset" title="Start a fresh session">Reset</button>
</header>
<main id="out" aria-live="polite">
  <div class="intro">
    You're typing into the same simulated shell an agent gets through its <code>bash</code> tool.
    Nothing touches your machine. Try:
    <div class="chips" id="chips"></div>
  </div>
</main>
<form id="form" autocomplete="off">
  <span class="prompt" id="prompt"></span>
  <textarea id="input" rows="1" spellcheck="false" aria-label="Command" autofocus></textarea>
</form>
<div class="hint">Enter runs · Shift+Enter new line · ↑/↓ history · Tab completes · Ctrl+L clears</div>
<script>
const out = document.getElementById('out'), input = document.getElementById('input');
const promptEl = document.getElementById('prompt'), form = document.getElementById('form');
let state = { user: 'agent', host: 'topwater', cwd: '~', home: '' };
let history = [], hIndex = 0, busy = false;
try { history = JSON.parse(localStorage.getItem('topwater-history') || '[]'); } catch (e) {}
hIndex = history.length;

const examples = ['help', 'ls -la', 'cat /etc/os-release', 'orders --status open | jq -r ".[].customer"',
  "grep -rn TODO ~/project | awk -F: '{print $1}' | sort | uniq -c",
  "awk -F, 'NR>1 {t[$2]+=$3} END {for (r in t) print r, t[r]}' ~/data/sales.csv | sort -k2,2nr",
  'for i in {1..3}; do echo "line $i"; done', 'sudo ls /root'];
const chips = document.getElementById('chips');
for (const ex of examples) {
  const b = document.createElement('button');
  b.type = 'button'; b.className = 'chip'; b.textContent = ex;
  b.onclick = () => run(ex);
  chips.appendChild(b);
}

function shortCwd(s) { return s.home && (s.cwd === s.home || s.cwd.startsWith(s.home + '/')) ? '~' + s.cwd.slice(s.home.length) : s.cwd; }
function promptNodes(s, cmd) {
  const span = document.createElement('span');
  span.className = 'prompt';
  const uh = document.createElement('span'); uh.className = 'uh'; uh.textContent = `${s.user}@${s.host}:`; span.append(uh);
  const p = document.createElement('span'); p.className = 'path'; p.textContent = shortCwd(s); span.append(p, '$ ');
  if (cmd !== undefined) { const c = document.createElement('span'); c.className = 'cmd'; c.textContent = cmd; span.append(c); }
  return span;
}
function renderPrompt() {
  promptEl.replaceChildren(...promptNodes(state).childNodes);
  document.getElementById('who').textContent = `${state.user}@${state.host}`;
}
function scrollDown() { out.scrollTop = out.scrollHeight; }
function autosize() { input.style.height = '1.5em'; input.style.height = input.scrollHeight + 'px'; }

async function api(path, body) {
  const r = await fetch(path, { method: body === undefined ? 'GET' : 'POST',
    headers: { 'Content-Type': 'application/json' }, body: body === undefined ? undefined : JSON.stringify(body) });
  if (!r.ok) throw new Error((await r.json().catch(() => ({}))).error || r.statusText);
  return r.json();
}

async function run(cmd) {
  if (busy) return;
  const entry = document.createElement('div'); entry.className = 'entry';
  entry.appendChild(promptNodes(state, cmd));
  out.appendChild(entry);
  input.value = ''; autosize();
  if (cmd.trim()) {
    if (history[history.length - 1] !== cmd) history.push(cmd);
    history = history.slice(-200); hIndex = history.length;
    try { localStorage.setItem('topwater-history', JSON.stringify(history)); } catch (e) {}
  } else { scrollDown(); return; }
  busy = true; form.classList.add('busy');
  try {
    const r = await api('/api/run', { command: cmd });
    for (const [text, cls] of [[r.stdout, ''], [r.stderr, 'err']]) {
      if (!text) continue;
      const pre = document.createElement('pre'); if (cls) pre.className = cls;
      pre.textContent = text.endsWith('\n') ? text.slice(0, -1) : text;
      entry.appendChild(pre);
    }
    if (r.exit_code !== 0) {
      const c = document.createElement('span'); c.className = 'code'; c.textContent = `exit ${r.exit_code}`;
      entry.appendChild(c);
    }
    state = r; renderPrompt();
  } catch (e) {
    const pre = document.createElement('pre'); pre.className = 'err'; pre.textContent = `[web terminal] ${e.message}`;
    entry.appendChild(pre);
  } finally { busy = false; form.classList.remove('busy'); input.focus(); scrollDown(); }
}

async function complete() {
  const line = input.value;
  let res;
  try { res = await api('/api/complete', { line }); } catch (e) { return; }
  const items = res.completions;
  if (!items.length) return;
  const words = line.split(' '), last = words[words.length - 1];
  let common = items[0];
  for (const it of items) while (!it.startsWith(common)) common = common.slice(0, -1);
  if (items.length === 1) common += common.endsWith('/') ? '' : ' ';
  if (common.length > last.length) { words[words.length - 1] = common; input.value = words.join(' '); autosize(); return; }
  const div = document.createElement('div'); div.className = 'entry';
  div.appendChild(promptNodes(state, line));
  const pre = document.createElement('pre'); pre.className = 'completions'; pre.textContent = items.join('  ');
  div.appendChild(pre); out.appendChild(div); scrollDown();
}

input.addEventListener('keydown', e => {
  if (e.key === 'Enter' && !e.shiftKey) { e.preventDefault(); run(input.value); }
  else if (e.key === 'Tab') { e.preventDefault(); complete(); }
  else if (e.key === 'ArrowUp' && !input.value.slice(0, input.selectionStart).includes('\n')) {
    if (hIndex > 0) { e.preventDefault(); input.value = history[--hIndex]; autosize(); }
  } else if (e.key === 'ArrowDown' && !input.value.slice(input.selectionEnd).includes('\n')) {
    if (hIndex < history.length) { e.preventDefault(); hIndex++; input.value = history[hIndex] || ''; autosize(); }
  } else if (e.key === 'l' && e.ctrlKey) { e.preventDefault(); clearScreen(); }
  else if (e.key === 'c' && e.ctrlKey && input.selectionStart === input.selectionEnd) {
    e.preventDefault(); const d = document.createElement('div'); d.className = 'entry';
    d.appendChild(promptNodes(state, input.value + '^C')); out.appendChild(d); input.value = ''; autosize(); scrollDown();
  }
});
input.addEventListener('input', autosize);
form.addEventListener('submit', e => { e.preventDefault(); run(input.value); });
out.addEventListener('click', () => { if (!getSelection().toString()) input.focus(); });

function clearScreen() { out.querySelectorAll('.entry').forEach(n => n.remove()); }
document.getElementById('clear').onclick = () => { clearScreen(); input.focus(); };
document.getElementById('help').onclick = () => run('help');
document.getElementById('reset').onclick = async () => {
  state = await api('/api/reset', {}); renderPrompt(); clearScreen();
  const d = document.createElement('div'); d.className = 'entry';
  const pre = document.createElement('pre'); pre.className = 'completions'; pre.textContent = '— new session —';
  d.appendChild(pre); out.appendChild(d); input.focus();
};

renderPrompt();
// ?run=cmd&run=cmd2 runs commands on load (shareable demo links)
api('/api/state').then(async s => {
  state = s; renderPrompt();
  for (const cmd of new URLSearchParams(location.search).getAll('run')) await run(cmd);
}).catch(() => renderPrompt());
</script>
</body>
</html>
"""


if __name__ == "__main__":
    main()
