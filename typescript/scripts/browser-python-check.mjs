// Checks python3 in headless Chromium: on a cross-origin isolated page (COOP/COEP) the library bundle loads the
// interpreter over HTTP, runs a program in a Web Worker and stops a busy loop at the time limit; on a page without those
// headers allowPython rejects with the explanation instead of hanging. Run after build:browser.
// Usage: node scripts/browser-python-check.mjs
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const root = fileURLToPath(new URL('..', import.meta.url));
const files = { '/topwater.js': `${root}dist-browser/topwater.js`, '/py/python.wasm.gz': `${root}../assets/python-wasm/python.wasm.gz`,
  '/py/python314.zip': `${root}../assets/python-wasm/python314.zip` };
const page = `<!doctype html><script type="module">
import { Shell } from './topwater.js';
window.result = (async () => {
  try {
    const sh = await new Shell({ maxCallDurationMs: 2000 }).allowPython({ artifacts: new URL('py/', location.href) });
    const a = await sh.run("python3 -c 'import sys, json; print(json.dumps(sys.argv[1:]))' x y");
    const b = await sh.run("python3 -c 'while True: pass'");
    return { out: a.stdout, loop: b.exitCode };
  } catch (e) { return { error: String(e.message) }; }
})();
</script>`;
const server = createServer(async (req, res) => {
  const path = new URL(req.url, 'http://x').pathname;
  const isolated = !path.startsWith('/open');
  const headers = isolated ? { 'cross-origin-opener-policy': 'same-origin', 'cross-origin-embedder-policy': 'require-corp' } : {};
  const file = files[path.replace(/^\/open/, '')];
  if (path === '/' || path === '/open/') return res.writeHead(200, { ...headers, 'content-type': 'text/html' }).end(page);
  if (!file) return res.writeHead(404).end();
  res.writeHead(200, { ...headers, 'content-type': path.endsWith('.js') ? 'text/javascript' : 'application/octet-stream' }).end(await readFile(file));
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
const origin = `http://127.0.0.1:${server.address().port}`;
const browser = await chromium.launch();
let failures = 0;
const check = (ok, what, got) => { console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}${ok ? '' : ': ' + JSON.stringify(got)}`); if (!ok) failures++; };
try {
  const p = await browser.newPage();
  await p.goto(origin + '/');
  const r = await p.waitForFunction(() => window.result, null, { timeout: 60_000 }).then(h => h.jsonValue());
  check(r.out === '["x", "y"]\n', 'python3 runs in a Web Worker on an isolated page', r);
  check(r.loop === 124, 'a busy loop stops at the time limit (124)', r);
  await p.goto(origin + '/open/');
  const o = await p.waitForFunction(() => window.result, null, { timeout: 60_000 }).then(h => h.jsonValue());
  check(/Cross-Origin-Opener-Policy/.test(o.error ?? ''), 'without COOP/COEP allowPython rejects with the reason', o);
} finally {
  await browser.close();
  server.close();
}
process.exit(failures ? 1 : 0);
