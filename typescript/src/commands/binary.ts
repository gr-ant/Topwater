import type { Shell } from '../shell.js';
import { Args, specOf, specMessage, ShellError, UsageException, inputBytes, type CommandContext } from '../command.js';
import { decodeBytes, fromBase64, toBase64, toHex } from '../bytes.js';
import { md5 } from '../md5.js';

function num(s: string): number {
  const n = /^0x[0-9a-f]+$/i.test(s) ? parseInt(s.slice(2), 16) : /^-?\d+$/.test(s) ? parseInt(s, 10) : NaN;
  if (!Number.isSafeInteger(n)) throw new ShellError(specMessage('xxd', 'invalidNumber', { value: s }));
  return n;
}

/** A negative start counts from the end; out-of-range bounds are clamped. */
function slice(data: Uint8Array, start: number, length?: number): Uint8Array {
  start = start < 0 ? Math.max(0, data.length + start) : Math.min(start, data.length);
  return data.subarray(start, length === undefined ? data.length : Math.min(data.length, start + Math.max(0, length)));
}

function readAll(ctx: CommandContext, files: string[], onError: () => void): Uint8Array {
  const parts = [...inputBytes(ctx, files, onError)].map(x => x[1]);
  if (parts.length === 1) return parts[0];
  const out = new Uint8Array(parts.reduce((n, p) => n + p.length, 0));
  let o = 0;
  for (const p of parts) { out.set(p, o); o += p.length; }
  return out;
}

const hex = toHex;
const latin1 = (b: Uint8Array) => Array.from(b, c => String.fromCharCode(c)).join('');
const sameBytes = (a: Uint8Array, b: Uint8Array) => a.length === b.length && a.every((x, i) => x === b[i]);
const printable = (c: Uint8Array) => Array.from(c, b => (b >= 32 && b < 127 ? String.fromCharCode(b) : '.')).join('');

/** [offset, chunk] per line; a null chunk is a "*" line where identical full lines repeat. */
function* lines(data: Uint8Array, width: number, squeeze: boolean): Generator<[number, Uint8Array | null]> {
  let prev: Uint8Array | null = null;
  let starred = false;
  for (let i = 0; i < data.length; i += width) {
    const chunk = data.subarray(i, Math.min(i + width, data.length));
    if (squeeze && prev && chunk.length === width && sameBytes(chunk, prev)) {
      if (!starred) { starred = true; yield [i, null]; }
      continue;
    }
    prev = chunk;
    starred = false;
    yield [i, chunk];
  }
}

const OD_ESCAPES: Record<number, string> = { 0: '\\0', 7: '\\a', 8: '\\b', 9: '\\t', 10: '\\n', 11: '\\v', 12: '\\f', 13: '\\r' };

/** A byte as cmp -b shows it: printable as itself, control bytes as ^X, high bytes as M- and the low byte's form. */
function visible(b: number): string {
  return b >= 128 ? 'M-' + visible(b - 128) : b < 32 ? '^' + String.fromCharCode(b + 64) : b === 127 ? '^?' : String.fromCharCode(b);
}

export function registerBinary(shell: Shell): void {
  shell.register('cmp', ctx => {
    const a = Args.parse(ctx.args, specOf('cmp'));
    const ops = a.positional;
    if (ops.length === 0) throw new UsageException(specMessage('cmp', 'missingOperand'));
    if (ops.length > 4) throw new UsageException(specMessage('cmp', 'extraOperand', { operand: ops[4] }));
    const number = (s: string | undefined, option: string) => {
      if (s === undefined) return 0;
      if (/^\s*[+-]?\d+\s*$/.test(s) && +s >= 0) return +s;
      throw new UsageException(specMessage('cmp', 'invalidNumber', { option, value: s }));
    };
    const i = a.value('i'), skips = (i ?? '').split(':');
    const skip1 = ops.length > 2 ? number(ops[2], 'ignore-initial') : number(i === undefined ? undefined : skips[0], 'ignore-initial');
    const skip2 = ops.length > 3 ? number(ops[3], 'ignore-initial') : number(i === undefined ? undefined : skips[skips.length - 1], 'ignore-initial');
    const limit = a.value('n') !== undefined ? number(a.value('n'), 'bytes') : undefined;
    const silent = a.has('s'), verbose = a.has('l'), bytes = a.has('b');
    const names = [ops[0], ops.length > 1 ? ops[1] : '-'];
    const data = [...inputBytes(ctx, names, () => {}, silent)].map(x => x[1]);
    if (data.length < 2) return 2;
    const x = slice(data[0], Math.min(skip1, data[0].length)), y = slice(data[1], Math.min(skip2, data[1].length));
    const common = Math.min(x.length, y.length), end = limit !== undefined ? Math.min(common, limit) : common;
    // shortcut: -l pads positions to the digits of the compared length, which matches GNU for ordinary files
    const longest = Math.max(x.length, y.length);
    const width = Math.max(1, String(limit !== undefined ? Math.min(longest, limit) : longest).length);
    const oct = (b: number) => b.toString(8).padStart(3);
    let line = 1, differ = false;
    for (let k = 0; k < end; k++) {
      if (x[k] === y[k]) { if (x[k] === 10) line++; continue; }
      differ = true;
      if (silent) return 1;
      if (!verbose) {
        const fields = { file1: names[0], file2: names[1], byte: String(k + 1), line: String(line) };
        ctx.stdout.write((bytes ? specMessage('cmp', 'differBytes', { ...fields, byte1: `${oct(x[k])} ${visible(x[k])}`, byte2: `${oct(y[k])} ${visible(y[k])}` })
          : specMessage('cmp', 'differ', fields)) + '\n');
        return 1;
      }
      const pos = String(k + 1).padStart(width);
      ctx.stdout.write((bytes ? `${pos} ${oct(x[k])} ${visible(x[k]).padEnd(4)} ${oct(y[k])} ${visible(y[k])}` : `${pos} ${oct(x[k])} ${oct(y[k])}`) + '\n');
    }
    if (x.length === y.length || (limit !== undefined && end >= limit)) return differ ? 1 : 0;
    if (!silent) {
      const [file, shorter] = x.length < y.length ? [names[0], x] : [names[1], y];
      const newlines = shorter.reduce((n, b) => n + (b === 10 ? 1 : 0), 0), byte = String(shorter.length);
      ctx.stderr.write('cmp: ' + (shorter.length === 0 ? specMessage('cmp', 'eofEmpty', { file })
        : verbose ? specMessage('cmp', 'eofVerbose', { file, byte })
          : shorter[shorter.length - 1] === 10 ? specMessage('cmp', 'eof', { file, byte, line: String(newlines) })
            : specMessage('cmp', 'eofInLine', { file, byte, line: String(newlines + 1) })) + '\n');
    }
    return 1;
  });

  shell.register('xxd', ctx => {
    const a = Args.parse(ctx.args, specOf('xxd'));
    let code = 0;
    let data = readAll(ctx, a.positional.slice(0, 1), () => code = 1); // a second operand (outfile) is not supported
    let start = num(a.value('s') ?? '0');
    data = slice(data, start, a.value('l') !== undefined ? num(a.value('l')!) : undefined);
    const cols = num(a.value('c') ?? (a.has('p') ? '30' : '16'));
    if (cols <= 0) throw new ShellError(specMessage('xxd', 'invalidColumns', {}));
    start = Math.max(0, start < 0 ? start + data.length : start);
    const fmt = (s: string) => (a.has('u') ? s.toUpperCase() : s);
    let out = '';
    for (let i = 0; i < data.length; i += cols) {
      const chunk = data.subarray(i, Math.min(i + cols, data.length));
      if (a.has('p')) { out += fmt(hex(chunk)) + '\n'; continue; }
      let h = '';
      for (let k = 0; k < chunk.length; k++) h += chunk[k].toString(16).padStart(2, '0') + (k % 2 === 1 ? ' ' : '');
      out += `${fmt((start + i).toString(16).padStart(8, '0'))}: ${fmt(h).padEnd(cols * 2 + Math.floor(cols / 2))} ${printable(chunk)}\n`;
    }
    ctx.stdout.write(out);
    return code;
  });

  shell.register('hexdump', ctx => {
    const a = Args.parse(ctx.args, specOf('hexdump'));
    let code = 0;
    const data = slice(readAll(ctx, a.positional, () => code = 1), num(a.value('s') ?? '0'), a.value('n') !== undefined ? num(a.value('n')!) : undefined);
    let out = '';
    for (const [i, chunk] of lines(data, 16, !a.has('v'))) {
      if (!chunk) { out += '*\n'; continue; }
      let h = '';
      chunk.forEach((b, k) => { h += b.toString(16).padStart(2, '0') + ' ' + (k === 7 ? ' ' : ''); });
      out += `${i.toString(16).padStart(8, '0')}  ${h.padEnd(49)} |${printable(chunk)}|\n`;
    }
    if (data.length > 0) out += data.length.toString(16).padStart(8, '0') + '\n';
    ctx.stdout.write(out);
    return code;
  });

  shell.register('od', ctx => {
    const a = Args.parse(ctx.args, specOf('od'));
    let code = 0, failed = 0;
    const data = slice(readAll(ctx, a.positional, () => { code = 1; failed++; }), num(a.value('j') ?? '0'), a.value('N') !== undefined ? num(a.value('N')!) : undefined);
    if (failed > 0 && failed === a.positional.length) return 1; // no input could be read: GNU od prints no offset either
    let fmt = a.value('t') ?? (a.has('c') ? 'c' : a.has('x') ? 'x2' : 'o2');
    if (fmt === 'x') fmt = 'x4'; else if (fmt === 'o') fmt = 'o4';
    let cells: (c: Uint8Array) => string;
    if (fmt === 'c') {
      cells = c => Array.from(c, b => (OD_ESCAPES[b] ?? (b >= 32 && b < 127 ? String.fromCharCode(b) : b.toString(8).padStart(3, '0'))).padStart(4)).join('');
    } else if (/^[xo][1248]$/.test(fmt)) {
      const size = +fmt[1], digits = fmt[0] === 'x' ? size * 2 : Math.floor((size * 8 + 2) / 3), radix = fmt[0] === 'x' ? 16 : 8;
      cells = c => {
        const p = new Uint8Array(Math.ceil(c.length / size) * size); // a partial last word is zero-padded
        p.set(c);
        let s = '';
        for (let k = 0; k < p.length; k += size) {
          let w = 0n;
          for (let j = size - 1; j >= 0; j--) w = (w << 8n) | BigInt(p[k + j]);
          s += ' ' + w.toString(radix).padStart(digits, '0');
        }
        return s;
      };
    } else throw new ShellError(specMessage('od', 'invalidType', { type: fmt }));
    const radix = a.value('A') ?? 'o';
    if (!['o', 'x', 'd', 'n'].includes(radix)) throw new ShellError(specMessage('od', 'invalidRadix', { radix }));
    const addr = (v: number) => radix === 'n' ? '' : radix === 'o' ? v.toString(8).padStart(7, '0') : radix === 'x' ? v.toString(16).padStart(6, '0') : v.toString().padStart(7, '0');
    let out = '';
    for (const [i, chunk] of lines(data, 16, !a.has('v'))) out += (chunk ? addr(i) + cells(chunk) : '*') + '\n';
    if (radix !== 'n') out += addr(data.length) + '\n';
    ctx.stdout.write(out);
    return code;
  });

  shell.register('strings', ctx => {
    const a = Args.parse(Args.numericShorthand(ctx.args), specOf('strings'));
    let code = 0;
    const min = num(a.value('n') ?? '4');
    let out = '';
    for (const [, data] of inputBytes(ctx, a.positional, () => code = 1)) {
      let run = '';
      for (let i = 0; i <= data.length; i++) {
        const b = i < data.length ? data[i] : 0;
        if ((b >= 32 && b < 127) || b === 9) { run += String.fromCharCode(b); continue; }
        if (run.length >= min) out += run + '\n';
        run = '';
      }
    }
    ctx.stdout.write(out);
    return code;
  });

  shell.register('base64', ctx => {
    const a = Args.parse(ctx.args, specOf('base64'));
    let code = 0;
    const data = readAll(ctx, a.positional, () => code = 1);
    if (a.has('d')) {
      let s = latin1(data);
      s = a.has('i') ? s.replace(/[^A-Za-z0-9+/=]/g, '') : s.replace(/\s/g, '');
      if (!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(s)) throw new ShellError(specMessage('base64', 'invalidInput', {}));
      ctx.stdout.write(decodeBytes(fromBase64(s)));
      return code;
    }
    const w = a.value('w') ?? '76';
    if (!/^\d+$/.test(w)) throw new ShellError(specMessage('base64', 'invalidWrap', { size: w }));
    const wrap = +w;
    const enc = toBase64(data);
    ctx.stdout.write(wrap > 0 ? enc.replace(new RegExp(`.{1,${wrap}}`, 'g'), '$&\n') : enc + '\n');
    return code;
  });

  for (const [name, algo] of [['md5sum', 'md5'], ['sha1sum', 'SHA-1'], ['sha256sum', 'SHA-256'], ['sha512sum', 'SHA-512']]) {
    shell.register(name, async ctx => {
      let code = 0;
      const files = ctx.args.filter(x => !x.startsWith('-') || x === '-');
      let out = '';
      for (const [n, data] of inputBytes(ctx, files, () => code = 1)) {
        const sum = algo === 'md5' ? md5(data) : toHex(new Uint8Array(await crypto.subtle.digest(algo, data as never)));
        out += `${sum}  ${files.length === 0 ? '-' : n}\n`;
      }
      ctx.stdout.write(out);
      return code;
    });
  }
}
