// Extracts the [InlineData(...)] rows of the C# xUnit theories into test/data/<File>.json, so the TypeScript port is held
// to the same tables (recorded from real bash / GNU tools). Run from typescript/: node scripts/extract-tests.mjs
import { readFileSync, writeFileSync, readdirSync } from 'node:fs';
import { join } from 'node:path';

const src = new URL('../../tests/Topwater.Tests/', import.meta.url).pathname;
const out = new URL('../test/data/', import.meta.url).pathname;

/** Parses the argument list of one InlineData(...) starting at text[i] (just after the '('). */
function parseArgs(text, i) {
  const args = [];
  for (;;) {
    while (/\s|,/.test(text[i])) i++;
    if (text[i] === ')') return [args, i + 1];
    if (text.startsWith('"""', i)) { // single-line raw string
      const end = text.indexOf('"""', i + 3);
      args.push(text.slice(i + 3, end));
      i = end + 3;
      continue;
    }
    if (text[i] === '@' && text[i + 1] === '"') { // verbatim: "" is a quote, no escapes
      let s = '';
      for (i += 2; ; i++) {
        if (text[i] === '"' && text[i + 1] === '"') { s += '"'; i++; continue; }
        if (text[i] === '"') break;
        s += text[i];
      }
      args.push(s);
      i++;
      continue;
    }
    if (text[i] === '"') {
      let s = '';
      for (;;) {
        i++;
        for (; text[i] !== '"'; i++) {
        if (text[i] !== '\\') { s += text[i]; continue; }
        const c = text[++i];
        const simple = { n: '\n', t: '\t', r: '\r', '0': '\0', '\\': '\\', '"': '"', "'": "'", a: '\x07', b: '\b', f: '\f', v: '\v', e: '\x1b' };
        if (c in simple) s += simple[c];
        else if (c === 'u') { s += String.fromCharCode(parseInt(text.slice(i + 1, i + 5), 16)); i += 4; }
        else if (c === 'x') {
          const m = /^[0-9a-fA-F]{1,4}/.exec(text.slice(i + 1));
          s += String.fromCharCode(parseInt(m[0], 16)); i += m[0].length;
        } else throw new Error('escape \\' + c);
        }
        i++;
        // "a" + "b" concatenation, possibly across lines
        const rest = /^\s*\+\s*"/.exec(text.slice(i));
        if (!rest) break;
        i += rest[0].length - 1;
      }
      args.push(s);
      continue;
    }
    const m = /^(-?[\d_]+|true|false|null)/.exec(text.slice(i));
    if (!m) throw new Error('cannot parse at ' + text.slice(i, i + 40));
    args.push(m[0] === 'true' ? true : m[0] === 'false' ? false : m[0] === 'null' ? null : Number(m[0].replaceAll("_", "")));
    i += m[0].length;
  }
}

let total = 0;
for (const file of readdirSync(src).filter(f => f.endsWith('.cs'))) {
  const text = readFileSync(join(src, file), 'utf8');
  const theories = {};
  let pending = [];
  let className = '';
  const re = /\bclass\s+(\w+)|\[InlineData\(|public\s+(?:async\s+)?\w+(?:<\w+>)?\s+(\w+)\s*\(/g;
  let m;
  while ((m = re.exec(text))) {
    if (m[1]) { className = m[1]; continue; }
    if (m[0].startsWith('[InlineData')) {
      const [args, end] = parseArgs(text, m.index + m[0].length);
      pending.push(args);
      re.lastIndex = end;
      continue;
    }
    if (pending.length) {
      theories[`${className}.${m[2]}`] = pending;
      total += pending.length;
      pending = [];
    }
  }
  if (Object.keys(theories).length) writeFileSync(join(out, file.replace(/\.cs$/, '.json')), JSON.stringify(theories, null, 1) + '\n');
}
console.log(`extracted ${total} rows`);
