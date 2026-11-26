// Builds the demo and checks it in headless Chromium: commands run and stream, an upload shows up in `ls`, the
// interpreter's own time limit answers with a trailer, and a call that can't be interrupted (a catastrophically
// backtracking regex) is hard-stopped by the page, which restarts the worker with the files it had. The site is served
// from a sub-path, as on GitHub Pages.
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
// served under a sub-path, as GitHub Pages serves it (/Topwater/), so an absolute asset path fails the check
const base = '/Topwater';
const server = createServer(async (req, res) => {
  const pathname = decodeURIComponent(new URL(req.url, 'http://x').pathname);
  if (!pathname.startsWith(base + '/')) { res.writeHead(404).end(); return; }
  const path = normalize(join(site, pathname.slice(base.length).replace(/\/$/, '/index.html')));
  if (!path.startsWith(site)) { res.writeHead(403).end(); return; }
  try { const body = await readFile(path); res.writeHead(200, { 'content-type': types[extname(path)] ?? 'application/octet-stream' }).end(body); }
  catch { res.writeHead(404).end(); }
});
await new Promise(r => server.listen(0, '127.0.0.1', r));
const origin = `http://127.0.0.1:${server.address().port}${base}`;

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

  // ---------- the recording viewer, on the bundled sample (recorded with the C# API) ----------
  const viewer = await browser.newPage({ viewport: { width: 1280, height: 900 } });
  const vErrors = [];
  viewer.on('console', m => { if (m.type() === 'error') vErrors.push(m.text()); });
  viewer.on('pageerror', e => vErrors.push(String(e)));
  viewer.on('request', r => { if (!r.url().startsWith(origin) && !r.url().startsWith('blob:') && !r.url().startsWith('data:')) foreign.push(r.url()); });
  await viewer.goto(`${origin}/replay.html`);
  const pos = () => viewer.locator('#pos').innerText();
  const settled = () => viewer.waitForFunction(() => !document.querySelector('#tree[aria-busy]') && document.querySelector('#tree ul'), null, { timeout: 30_000 });
  await viewer.waitForSelector('#viewer:not([hidden])', { timeout: 30_000 });
  await settled();
  check((await viewer.locator('#timeline li').count()) === 10 && (await pos()) === 'Turn 1 of 9', 'viewer: the sample loads as a timeline of 9 turns plus the start');
  const meta = await viewer.locator('#meta').innerText();
  check(meta.includes('compute total sales amount per region') && meta.includes('the ports differ'), 'viewer: the summary shows the recorded task, and that turns re-run here may differ from the C# recording');

  await viewer.locator('body').press('ArrowRight');
  await viewer.locator('body').press('ArrowRight');
  await settled();
  const turn3 = await viewer.locator('#turn').innerText();
  check((await pos()) === 'Turn 3 of 9' && turn3.includes('north 220') && turn3.includes("recording's own snapshot"), 'viewer: arrow keys step to turn 3, which shows its output and comes from a snapshot');
  const changes3 = await viewer.locator('#changes').innerText();
  check(/added\s+\/home\/agent\/report\.txt/i.test(changes3) && (await viewer.locator('#changes pre.diff-body').first().innerText()).includes('+north 220'), 'viewer: turn 3 lists report.txt as added, with its diff');

  await viewer.getByRole('button', { name: 'Next turn' }).click();
  await settled();
  const turn4 = await viewer.locator('#turn').innerText();
  const diff4 = await viewer.locator('#changes pre.diff-body').first().innerText();
  check(turn4.includes('rebuilt by re-running') && turn4.includes('same output'), 'viewer: turn 4 is rebuilt by re-running it in the browser, and its output matches the recording');
  check(diff4.includes('+region total') && diff4.includes(' north 220') && (await viewer.locator('#changes li.change').count()) === 1, 'viewer: turn 4 shows a text diff of report.txt (a header line added) and nothing else');

  await viewer.locator('#timeline button[data-index="6"]').click();
  await settled();
  const changes7 = await viewer.locator('#changes').innerText();
  check(/added\s+\/home\/agent\/archive\.tgz/i.test(changes7) && /removed\s+\/home\/agent\/archive\/sales-2026-11-25\.csv/i.test(changes7), 'viewer: turn 7 shows archive.tgz added and the archive folder removed');
  await viewer.locator('#tree button.file[data-path="/home/agent/archive.tgz"]').click();
  await viewer.waitForFunction(() => document.querySelector('#fileview')?.textContent?.includes('Binary'));
  await viewer.locator('#tree button.file[data-path="/home/agent/report.txt"]').click();
  await viewer.waitForFunction(() => document.querySelector('#fileview pre')?.textContent?.includes('North 220'));
  check(!(await viewer.locator('#tree button.file[data-path="/home/agent/archive/sales-2026-11-25.csv"]').count()), 'viewer: the file tree at turn 7 shows the files of that moment (the archive folder is gone; report.txt has the edits)');

  await viewer.getByRole('button', { name: 'Start: before the first turn' }).click();
  await settled();
  check((await pos()).startsWith('Start') && !(await viewer.locator('#tree button.file[data-path="/home/agent/report.txt"]').count()), 'viewer: the start has no report.txt yet');
  await viewer.locator('body').press('End');
  await settled();
  check((await pos()) === 'Turn 9 of 9', 'viewer: End jumps to the last turn');

  // fork from turn 6: the terminal opens on that machine and keeps going
  await viewer.locator('#timeline button[data-index="5"]').click();
  await settled();
  const [forked] = await Promise.all([viewer.context().waitForEvent('page'), viewer.getByRole('button', { name: 'Open in a new tab' }).click()]);
  const fErrors = [];
  forked.on('pageerror', e => fErrors.push(String(e)));
  await forked.waitForFunction(() => document.querySelector('[data-busy]')?.getAttribute('data-busy') === 'false' && document.querySelector('#prompt')?.textContent?.includes('agent@topwater'), null, { timeout: 30_000 });
  const fin = forked.getByRole('textbox').first();
  await fin.fill('cat report.txt; ls; echo more >> report.txt; tail -1 report.txt');
  await fin.press('Enter');
  await forked.waitForTimeout(50);
  await forked.waitForFunction(() => document.querySelector('[data-busy]')?.getAttribute('data-busy') === 'false');
  const forkOut = await forked.locator('[role="log"]').innerText();
  check(forkOut.includes('Forked from') && forkOut.includes('North 220') && forkOut.includes('archive') && forkOut.includes('more') && !forkOut.includes('notes.txt'), 'viewer: "Open in a new tab" opens the terminal on the machine after turn 6, and commands run on it');
  await forked.reload();
  await forked.waitForFunction(() => document.querySelector('[data-busy]')?.getAttribute('data-busy') === 'false' && document.querySelector('#prompt')?.textContent?.includes('agent@topwater'), null, { timeout: 30_000 });
  check((await forked.locator('[role="log"]').innerText()).includes('Forked from'), 'viewer: the forked terminal still has its machine after a reload');

  // compare any two moments: turn 1 against turn 7
  const changesSettled = text => viewer.waitForFunction(t => !document.querySelector('#changes[aria-busy]') && document.querySelector('#changes')?.textContent?.includes(t), text, { timeout: 30_000 });
  await viewer.selectOption('#cmp-a', '0:0');
  await viewer.selectOption('#cmp-z', '0:6');
  await changesSettled('archive.tgz');
  const cmp = await viewer.locator('#changes').innerText();
  check(/added\s+\/home\/agent\/report\.txt/i.test(cmp) && /added\s+\/home\/agent\/archive\.tgz/i.test(cmp) && cmp.includes('+North 220') && /Binary or very large file \(none → binary/.test(cmp),
    'viewer: comparing turn 1 with turn 7 lists every file added or removed between them, text diffs and binary files summarized');

  // continue from turn 6: the commands typed become branch 1
  await viewer.locator('#timeline button[data-index="5"]').click();
  await settled();
  await viewer.getByRole('button', { name: 'Continue from here' }).click();
  const liveCmd = viewer.locator('#live-cmd');
  check(await liveCmd.evaluate(e => e === document.activeElement), 'viewer: "Continue from here" opens a command line and focuses it');
  const typeLive = async (cmd, pos) => { await liveCmd.fill(cmd); await liveCmd.press('Enter'); await viewer.waitForFunction(p => document.querySelector('#pos')?.textContent === p, pos, { timeout: 30_000 }); await settled(); };
  await typeLive('echo branch > b.txt; cat report.txt', 'Turn 7 of 7 · Branch 1');
  await typeLive('rm report.txt; ls', 'Turn 8 of 8 · Branch 1');
  const liveText = await viewer.locator('#live-out').innerText();
  check(liveText.includes('North 220') && liveText.includes('b.txt') && (await viewer.locator('#timeline li').count()) === 9 && (await viewer.locator('#timeline .typed').count()) === 2,
    'viewer: commands run on the machine after turn 6 and become turns 7 and 8 of a new branch');
  check(/removed\s+\/home\/agent\/report\.txt/i.test(await viewer.locator('#changes').innerText()) && (await viewer.locator('#branches li').count()) === 2, 'viewer: the branch turn shows what it changed, and the timelines list the original and branch 1');

  // switch between them, from the keyboard too
  await viewer.getByRole('button', { name: /^Original/ }).click();
  await settled();
  check((await pos()) === 'Turn 9 of 9' && (await viewer.locator('#timeline .typed').count()) === 0, 'viewer: switching to the original shows its 9 turns, unchanged');
  await viewer.getByRole('button', { name: /^Branch 1 after/ }).focus();
  await viewer.keyboard.press('Enter');
  await settled();
  check((await pos()) === 'Turn 8 of 8 · Branch 1' && await viewer.evaluate(() => document.activeElement?.matches('.branch-btn[aria-current]') && document.activeElement.textContent.startsWith('Branch 1')),
    'viewer: switching back to branch 1 with the keyboard, which stays on the chosen timeline');

  // diff across branches: the original's last turn against the branch's
  await viewer.selectOption('#cmp-a', '0:8');
  await viewer.selectOption('#cmp-z', '1:7');
  await changesSettled('b.txt');
  const across = await viewer.locator('#changes').innerText();
  check(/added\s+\/home\/agent\/b\.txt/i.test(across) && /removed\s+\/home\/agent\/report\.txt/i.test(across), 'viewer: comparing the original\'s last turn with the branch\'s shows b.txt added and report.txt removed');

  // download the branch, then open the file it saved
  const [branchDl] = await Promise.all([viewer.waitForEvent('download', { timeout: 30_000 }), viewer.getByRole('button', { name: 'Download branch 1 as a .topwater file' }).click()]);
  const branchFile = join(root, 'browser/dist/sales-report-branch-1.topwater');
  await branchDl.saveAs(branchFile);
  check(branchDl.suggestedFilename() === 'sales-report-branch-1.topwater', 'viewer: a branch downloads as a .topwater file');
  await viewer.locator('#file').setInputFiles(branchFile);
  await viewer.waitForFunction(() => document.querySelector('#pos')?.textContent === 'Turn 1 of 8', null, { timeout: 30_000 });
  await settled();
  await viewer.locator('body').press('End');
  await settled();
  const reopened = await viewer.locator('#turn').innerText();
  check(reopened.includes('rm report.txt; ls') && (await viewer.locator('#meta').innerText()).includes('6') && (await viewer.locator('#branches li').count()) === 1 &&
    !(await viewer.locator('#tree button.file[data-path="/home/agent/report.txt"]').count()) && (await viewer.locator('#tree button.file[data-path="/home/agent/b.txt"]').count()) === 1,
    'viewer: the downloaded branch opens as a recording of 8 turns, ending on the branch\'s machine');
  // a .topwater file (the cross-port fixture recorded by this port): opens from the file picker
  await viewer.locator('#file').setInputFiles(join(root, '../tests/Topwater.Tests/recordings/from-typescript.topwater'));
  await viewer.waitForFunction(() => document.querySelector('#pos')?.textContent === 'Turn 1 of 3', null, { timeout: 30_000 });
  await settled();
  await viewer.locator('#timeline button[data-index="1"]').click();
  await settled();
  const meta3 = await viewer.locator('#meta').innerText();
  check(meta3.includes('cross-port fixture') && !meta3.includes('Re-run here') && (await viewer.locator('#turn').innerText()).includes('hi you'), 'viewer: a .topwater file opens, with its turns and metadata');
  check(vErrors.length === 0 && fErrors.length === 0, `viewer: no console errors${vErrors.length + fErrors.length ? ': ' + [...vErrors, ...fErrors].join(' | ') : ''}`);
  await viewer.setViewportSize({ width: 375, height: 800 });
  const vOverflow = await viewer.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
  check(vOverflow <= 0, `viewer: no horizontal scroll at 375 px (overflow ${vOverflow}px)`);
  check(foreign.length === 0, `viewer: no requests outside the page's origin${foreign.length ? ': ' + foreign.join(' ') : ''}`);

  if (shots) {
    await mkdir(shots, { recursive: true });
    for (const scheme of ['light', 'dark']) {
      const p = await browser.newPage({ viewport: { width: 1280, height: 900 }, colorScheme: scheme });
      await p.goto(`${origin}/replay.html`);
      await p.waitForSelector('#viewer:not([hidden])');
      await p.locator('#timeline button[data-index="3"]').click();
      await p.waitForFunction(() => document.querySelector('#changes pre.diff-body'));
      await p.screenshot({ path: join(shots, `replay-${scheme}.png`), fullPage: true });
      const phone = await browser.newPage({ viewport: { width: 375, height: 812 }, colorScheme: scheme, deviceScaleFactor: 2 });
      await phone.goto(`${origin}/replay.html`);
      await phone.waitForSelector('#viewer:not([hidden])');
      await phone.waitForFunction(() => document.querySelector('#tree ul'));
      await phone.evaluate(() => window.topwaterReplay.go(3));
      await phone.waitForFunction(() => document.querySelector('#changes pre.diff-body'));
      await phone.screenshot({ path: join(shots, `replay-${scheme}-phone.png`), fullPage: true });
    }
    for (const scheme of ['light', 'dark']) {
      const p = await browser.newPage({ viewport: { width: 1280, height: 900 }, colorScheme: scheme });
      await p.goto(`${origin}/`);
      await p.waitForFunction(() => document.querySelector('[data-busy]')?.getAttribute('data-busy') === 'false');
      const box = p.getByRole('textbox').first();
      for (const cmd of ['ls -la', "jq '.[] | select(.status==\"open\") | .id' data/orders.json", 'for n in 8 16 32 64; do echo "2^$n = $((2**n))"; done']) {
        await box.fill(cmd); await box.press('Enter'); await p.waitForTimeout(50);
        await p.waitForFunction(() => document.querySelector('[data-busy]')?.getAttribute('data-busy') === 'false');
      }
      await p.screenshot({ path: join(shots, `demo-${scheme}.png`), fullPage: true });
      const phone = await browser.newPage({ viewport: { width: 375, height: 812 }, colorScheme: scheme, deviceScaleFactor: 2 });
      await phone.goto(`${origin}/`);
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
