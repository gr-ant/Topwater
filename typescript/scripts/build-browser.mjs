// Builds the browser artifacts:
//   dist-browser/topwater.js   the library as one ES module for browsers (same API as the Node package)
//   dist-browser/worker.js     a ready-made Web Worker that runs a shell (see browser/session.ts)
//   browser/dist/              the demo (terminal and recording viewer): a folder of static files that works from any static host
import { build } from 'esbuild';
import { cpSync, mkdirSync, rmSync, statSync } from 'node:fs';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('..', import.meta.url));
const common = { bundle: true, format: 'esm', platform: 'browser', target: 'es2022', minify: true, legalComments: 'none', logLevel: 'warning' };

rmSync(`${root}dist-browser`, { recursive: true, force: true });
rmSync(`${root}browser/dist`, { recursive: true, force: true });
await build({ ...common, entryPoints: { topwater: `${root}src/index.ts`, worker: `${root}browser/worker.ts` }, outdir: `${root}dist-browser` });
await build({ ...common, entryPoints: { main: `${root}browser/demo/main.ts`, worker: `${root}browser/worker.ts`, replay: `${root}browser/demo/replay.ts`, 'replay-worker': `${root}browser/replay-worker.ts` }, outdir: `${root}browser/dist` });

const site = `${root}browser/dist`;
for (const f of ['index.html', 'style.css', 'replay.html', 'replay.css']) cpSync(`${root}browser/demo/${f}`, `${site}/${f}`);
cpSync(`${root}browser/demo/samples`, `${site}/samples`, { recursive: true });
mkdirSync(`${site}/brand`, { recursive: true });
for (const f of ['topwater_lockup.svg', 'topwater_lockup_dark.svg', 'topwater_mark.svg', 'topwater_icon_512.svg', 'favicon_32.svg', 'favicon_32.png', 'topwater_icon_128.png'])
  cpSync(`${root}../assets/brand/${f}`, `${site}/brand/${f}`);

const kb = p => (statSync(p).size / 1024).toFixed(0) + ' KB';
console.log(`dist-browser/topwater.js ${kb(`${root}dist-browser/topwater.js`)}, browser/dist/worker.js ${kb(`${site}/worker.js`)}, browser/dist/main.js ${kb(`${site}/main.js`)}, browser/dist/replay-worker.js ${kb(`${site}/replay-worker.js`)}`);
