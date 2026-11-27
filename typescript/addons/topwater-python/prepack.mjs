// npm packs no symbolic links: copies the interpreter (assets/python-wasm; python.wasm.gz is written by
// tools/python_wasm_gz.py) and the licence into this folder before packing.
import { copyFileSync, mkdirSync } from 'node:fs';

const assets = new URL('../../../assets/python-wasm/', import.meta.url);
const here = new URL('./', import.meta.url);
mkdirSync(new URL('interpreter/', here), { recursive: true });
for (const f of ['python.wasm.gz', 'python314.zip', 'MANIFEST', 'PROVENANCE', 'THIRD_PARTY_LICENSES.md'])
  copyFileSync(new URL(f, assets), new URL(`interpreter/${f}`, here));
copyFileSync(new URL('../../../LICENSE', import.meta.url), new URL('LICENSE', here));
