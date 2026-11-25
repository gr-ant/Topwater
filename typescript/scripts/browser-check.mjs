// Builds the demo and checks it in headless Chromium: commands run and stream, an upload shows up in `ls`, the
// interpreter's own time limit answers with a trailer, and a call that can't be interrupted (a catastrophically
// backtracking regex) is hard-stopped by the page, which restarts the worker with the files it had.
// Usage: node scripts/browser-check.mjs [screenshot-dir]
import { execFileSync } from 'node:child_process';
import { createServer } from 'node:http';
import { readFile, mkdir } from 'node:fs/promises';
import { extname, join, normalize } from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright';

const root = fileURLToPath(new URL('..', import.meta.url));
const shots = process.argv[2];
execFileSync(process.execPath, [join(root, 'scripts/build-browser.mjs')], { stdio: 'inherit' });

const site = join(root, 'browser/dist');
const types = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.svg': 'image/svg+xml', '.png': 'image/png' };
const server = createServer(async (req, res) => {
  const path = normalize(join(site, decodeURIComponent(new URL(req.url, 'http://x').pathname).replace(/\/$/, '/index.html')));
  if (!path.startsWith(site)) { res.writeHead(403).end(); return; }
  try { const body = await readFile(path); res.writeHead(200, { 'content-type': types[extname(path)] ?? 'application/octet-stream' }).end(body); }
  catch { res.writeHead(404).end(); }
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
const origin = `http://127.0.0.1:${server.address().port}`;

let failures = 0;
const check = (ok, what) => { console.log(`${ok ? 'ok  ' : 'FAIL'} ${what}`); if (!ok) failures++; };

const browser = await chromium.launch();
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const errors = [], foreign = [];
  page.on('console', m => { if (m.type() === 'error') errors.push(m.text()); });
  page.on('pageerror', e => errors.push(String(e)));
  page.on('request', r => { if (!r.url().startsWith(origin) && !r.url().startsWith('blob:') && !r.url().startsWith('data:')) foreign.push(r.url()); });

  await page.goto(`${origin}/?limit=2`);
  const term = page.locator('[data-busy]');
  const input = page.getByRole('textbox').first();
  const idle = () => page.waitForFunction(() => document.querySelector('[data-busy]')?.getAttribute('data-busy') === 'false', null, { timeout: 30_000 });
  const output = () => page.locator('[role="log"]').innerText();
  const type = async cmd => { await idle(); await input.fill(cmd); await input.press('Enter'); await page.waitForTimeout(50); await idle(); };
  await idle();

  await type('ls');
  check(/notes\.txt/.test(await output()), '`ls` lists the seed files');
  await type('echo hi | tr a-z A-Z; false');
  const out1 = await output();
  check(out1.includes('HI') && out1.includes('[exit code 1]'), 'pipeline output and the [exit code N] line');
  await type('for n in 8 16 32 64; do echo "2^$n = $((2**n))"; done');
  check((await output()).includes('2^64 = 0'), 'loops and 64-bit arithmetic');
  await type('tar czf /tmp/t.tgz -C ~ data && tar tzf /tmp/t.tgz | sort | head -3');
  check((await output()).includes('data/orders.json'), 'tar czf / tzf round trip (CompressionStream + inflate)');
  await type('printf abc | sha256sum; printf abc | md5sum');
  const out2 = await output();
  check(out2.includes('ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad') && out2.includes('900150983cd24fb0d6963f7d28e17f72'), 'sha256sum (Web Crypto) and md5sum');

  // history: ArrowUp brings back the last command
  await input.fill('');
  await input.press('ArrowUp');
  check((await input.inputValue()).includes('md5sum'), 'ArrowUp recalls history');
  await input.fill('');

  // upload through the real file input, binary-safe
  const bytes = Buffer.from([0, 1, 2, 255, 254, 10, 0x80, 0x41]);
  await page.locator('input[type=file]').setInputFiles({ name: 'blob.bin', mimeType: 'application/octet-stream', buffer: bytes });
  await page.waitForFunction(() => document.querySelector('[role="log"]')?.textContent?.includes('blob.bin'));
  await type('ls -la blob.bin && od -An -tx1 blob.bin');
  const out3 = await output();
  check(/ 8 .*blob\.bin/.test(out3) && out3.includes('00 01 02 ff fe 0a 80 41'), 'uploaded file appears in `ls` with its exact bytes');

  // download: `download FILE` hands the file to the browser
  const [dl] = await Promise.all([page.waitForEvent('download', { timeout: 10_000 }), type('download blob.bin')]);
  check(dl.suggestedFilename() === 'blob.bin', 'download FILE saves it in the browser');

  // the interpreter's own limit: a busy loop ends with a trailer and the shell stays up
  await type('while :; do :; done');
  check(/\[stopped: /.test(await output()), 'a busy loop is stopped by the shell with a [stopped: ...] trailer');

  // a regex match can't be interrupted from inside: the page terminates the worker and restores the last snapshot
  await type('echo keep > kept.txt; X=42');
  const t0 = Date.now();
  await type("printf 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaab\\n' | grep -E '^(a|aa)+$'");
  const waited = Date.now() - t0;
  const out4 = await output();
  check(out4.includes('restarted the shell') && waited < 10_000, `hard stop: the page restarted the worker after ${waited} ms`);
  await type('cat kept.txt; echo "X=$X"; pwd');
  const out5 = await output();
  check(out5.includes('keep') && out5.includes('X=42'), 'after the hard stop, files and variables are restored');

  check(errors.length === 0, `no console errors${errors.length ? ': ' + errors.join(' | ') : ''}`);
  check(foreign.length === 0, `no requests outside the page's origin${foreign.length ? ': ' + foreign.join(' ') : ''}`);

  // phone width: no horizontal page scroll
  await page.setViewportSize({ width: 375, height: 800 });
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  check(overflow <= 0, `no horizontal scroll at 375 px (overflow ${overflow}px)`);

  if (shots) {
    await mkdir(shots, { recursive: true });
    for (const scheme of ['light', 'dark']) {
      const p = await browser.newPage({ viewport: { width: 1280, height: 900 }, colorScheme: scheme });
      await p.goto(origin);
      await p.waitForFunction(() => document.querySelector('[data-busy]')?.getAttribute('data-busy') === 'false');
      const box = p.getByRole('textbox').first();
      for (const cmd of ['ls -la', "jq '.[] | select(.status==\"open\") | .id' data/orders.json", 'for n in 8 16 32 64; do echo "2^$n = $((2**n))"; done']) {
        await box.fill(cmd); await box.press('Enter'); await p.waitForTimeout(50);
        await p.waitForFunction(() => document.querySelector('[data-busy]')?.getAttribute('data-busy') === 'false');
      }
      await p.screenshot({ path: join(shots, `demo-${scheme}.png`), fullPage: true });
      const phone = await browser.newPage({ viewport: { width: 375, height: 812 }, colorScheme: scheme, deviceScaleFactor: 2 });
      await phone.goto(origin);
      await phone.waitForFunction(() => document.querySelector('[data-busy]')?.getAttribute('data-busy') === 'false');
      await phone.screenshot({ path: join(shots, `demo-${scheme}-phone.png`), fullPage: true });
    }
    console.log(`screenshots in ${shots}`);
  }
} finally {
  await browser.close();
  server.close();
}
if (failures) { console.log(`${failures} check(s) failed`); process.exit(1); }
console.log('all browser checks passed');
