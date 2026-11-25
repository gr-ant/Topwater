import { createHash } from 'node:crypto';
import type { Shell } from '../shell.js';
import { Args, ShellError, inputBytes, type CommandContext } from '../command.js';
import { decodeBytes } from '../bytes.js';

function num(s: string): number {
  const n = /^0x[0-9a-f]+$/i.test(s) ? parseInt(s.slice(2), 16) : /^-?\d+$/.test(s) ? parseInt(s, 10) : NaN;
  if (!Number.isSafeInteger(n)) throw new ShellError(`invalid number '${s}'`);
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

const hex = (b: Uint8Array) => Buffer.from(b).toString('hex');
const printable = (c: Uint8Array) => Array.from(c, b => (b >= 32 && b < 127 ? String.fromCharCode(b) : '.')).join('');

/** [offset, chunk] per line; a null chunk is a "*" line where identical full lines repeat. */
function* lines(data: Uint8Array, width: number, squeeze: boolean): Generator<[number, Uint8Array | null]> {
  let prev: Uint8Array | null = null;
  let starred = false;
  for (let i = 0; i < data.length; i += width) {
    const chunk = data.subarray(i, Math.min(i + width, data.length));
    if (squeeze && prev && chunk.length === width && Buffer.compare(chunk, prev) === 0) {
      if (!starred) { starred = true; yield [i, null]; }
      continue;
    }
    prev = chunk;
    starred = false;
    yield [i, chunk];
  }
}

const OD_ESCAPES: Record<number, string> = { 0: '\\0', 7: '\\a', 8: '\\b', 9: '\\t', 10: '\\n', 11: '\\v', 12: '\\f', 13: '\\r' };

export function registerBinary(shell: Shell): void {
  shell.register('xxd', ctx => {
    const a = Args.parse(ctx.args, 'pu', 'lsc', 'plain=p,len=l,seek=s,cols=c');
    let code = 0;
    let data = readAll(ctx, a.positional.slice(0, 1), () => code = 1); // a second operand (outfile) is not supported
    let start = num(a.value('s') ?? '0');
    data = slice(data, start, a.value('l') !== undefined ? num(a.value('l')!) : undefined);
    const cols = num(a.value('c') ?? (a.has('p') ? '30' : '16'));
    if (cols <= 0) throw new ShellError('invalid number of columns');
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
  }, 'Make a hex dump (-p plain, -l LEN, -s OFFSET)');

  shell.register('hexdump', ctx => {
    const a = Args.parse(ctx.args, 'Cv', 'ns', 'canonical=C,length=n,skip=s,no-squeezing=v');
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
  }, 'Display file contents in hexadecimal (-C canonical, -n LEN)');

  shell.register('od', ctx => {
    const a = Args.parse(ctx.args, 'cxov', 'ANtj', 'address-radix=A,read-bytes=N,format=t,skip-bytes=j');
    let code = 0;
    const data = slice(readAll(ctx, a.positional, () => code = 1), num(a.value('j') ?? '0'), a.value('N') !== undefined ? num(a.value('N')!) : undefined);
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
    } else throw new ShellError(`invalid type string '${fmt}' (supported here: c, x1, x2, x4, x8, o1, o2, o4, o8)`);
    const radix = a.value('A') ?? 'o';
    if (!['o', 'x', 'd', 'n'].includes(radix)) throw new ShellError(`invalid output address radix '${radix}'; it must be one character from [doxn]`);
    const addr = (v: number) => radix === 'n' ? '' : radix === 'o' ? v.toString(8).padStart(7, '0') : radix === 'x' ? v.toString(16).padStart(6, '0') : v.toString().padStart(7, '0');
    let out = '';
    for (const [i, chunk] of lines(data, 16, !a.has('v'))) out += (chunk ? addr(i) + cells(chunk) : '*') + '\n';
    if (radix !== 'n') out += addr(data.length) + '\n';
    ctx.stdout.write(out);
    return code;
  }, 'Dump files in octal and other formats (-c, -x, -t x1, -An, -N LEN)');

  shell.register('strings', ctx => {
    const a = Args.parse(Args.numericShorthand(ctx.args), 'a', 'n', 'bytes=n,all=a');
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
  }, 'Print printable character sequences in files (-n MIN)');

  shell.register('base64', ctx => {
    const a = Args.parse(ctx.args, 'di', 'w', 'decode=d,ignore-garbage=i,wrap=w');
    let code = 0;
    const data = readAll(ctx, a.positional, () => code = 1);
    if (a.has('d')) {
      let s = Buffer.from(data).toString('latin1');
      s = a.has('i') ? s.replace(/[^A-Za-z0-9+/=]/g, '') : s.replace(/\s/g, '');
      if (!/^(?:[A-Za-z0-9+/]{4})*(?:[A-Za-z0-9+/]{2}==|[A-Za-z0-9+/]{3}=)?$/.test(s)) throw new ShellError('invalid input');
      ctx.stdout.write(decodeBytes(Buffer.from(s, 'base64')));
      return code;
    }
    const w = a.value('w') ?? '76';
    if (!/^\d+$/.test(w)) throw new ShellError(`invalid wrap size: '${w}'`);
    const wrap = +w;
    const enc = Buffer.from(data).toString('base64');
    ctx.stdout.write(wrap > 0 ? enc.replace(new RegExp(`.{1,${wrap}}`, 'g'), '$&\n') : enc + '\n');
    return code;
  }, 'Base64 encode/decode');

  for (const [name, algo] of [['md5sum', 'md5'], ['sha1sum', 'sha1'], ['sha256sum', 'sha256'], ['sha512sum', 'sha512']]) {
    shell.register(name, ctx => {
      let code = 0;
      const files = ctx.args.filter(x => !x.startsWith('-') || x === '-');
      let out = '';
      for (const [n, data] of inputBytes(ctx, files, () => code = 1)) out += `${createHash(algo).update(data).digest('hex')}  ${files.length === 0 ? '-' : n}\n`;
      ctx.stdout.write(out);
      return code;
    }, `Compute ${algo.toUpperCase()} checksums`);
  }
}
