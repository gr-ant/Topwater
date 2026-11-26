// Text formatting shared by the shell and its commands: printf, %q quoting, backslash escapes, strftime.

/** Backslash escapes: \n \t ..., \xHH, and octal (\NNN, or \0NNN for echo -e). */
export function unescape(s: string, keepUnknown = false, echo = false): string {
  let out = '';
  const isOct = (c: string | undefined) => c !== undefined && c >= '0' && c <= '7';
  const isHex = (c: string | undefined) => c !== undefined && /[0-9a-fA-F]/.test(c);
  for (let i = 0; i < s.length; i++) {
    if (s[i] !== '\\' || i + 1 >= s.length) { out += s[i]; continue; }
    const n = s[i + 1];
    if (isOct(n) && (!echo || n === '0')) {
      const j = echo ? i + 2 : i + 1;
      let k = j;
      while (k < s.length && k < j + 3 && isOct(s[k])) k++;
      out += byteChar(k > j ? parseInt(s.slice(j, k), 8) & 0xff : 0);
      i = k - 1;
    } else if (n === 'x' && isHex(s[i + 2])) {
      let k = i + 2;
      while (k < s.length && k < i + 4 && isHex(s[k])) k++;
      out += byteChar(parseInt(s.slice(i + 2, k), 16));
      i = k - 1;
    } else if ((n === 'u' || n === 'U') && isHex(s[i + 2])) {
      let k = i + 2;
      while (k < s.length && k < i + 2 + (n === 'u' ? 4 : 8) && isHex(s[k])) k++;
      out += String.fromCodePoint(parseInt(s.slice(i + 2, k), 16));
      i = k - 1;
    } else {
      i++;
      const known: Record<string, string> = { n: '\n', t: '\t', r: '\r', '\\': '\\', a: '\x07', b: '\b', f: '\f', v: '\v', e: '\x1b', E: '\x1b', '"': '"' };
      out += known[n] ?? (keepUnknown ? '\\' + n : n);
    }
  }
  return out;
}

/** A byte value as a character: ASCII as is, 0x80-0xff as the escaped raw byte (see bytes.ts). */
export function byteChar(b: number): string { return b < 0x80 ? String.fromCharCode(b) : String.fromCharCode(0xdc00 + b); }

const SHELL_SAFE = /^[A-Za-z0-9_./+:,=@%^-]+$/;

/** printf %q: quotes a string so bash would read it back as one word. */
export function shellQuote(s: string): string {
  if (s.length === 0) return "''";
  if (SHELL_SAFE.test(s)) return s;
  // eslint-disable-next-line no-control-regex
  if (/[\x00-\x1f\x7f]/.test(s)) {
    let out = "$'";
    for (const c of s) {
      const code = c.charCodeAt(0);
      out += c === '\\' ? '\\\\' : c === "'" ? "\\'" : c === '\n' ? '\\n' : c === '\t' ? '\\t' : c === '\r' ? '\\r' : c === '\x1b' ? '\\E'
        : code < 32 || code === 127 ? '\\' + code.toString(8).padStart(3, '0') : c;
    }
    return out + "'";
  }
  let out = '';
  for (const c of s) {
    if (!(/[A-Za-z0-9]/.test(c) || c.charCodeAt(0) > 127 || '_./+:,=@%^-'.includes(c))) out += '\\';
    out += c;
  }
  return out;
}

/** ${x@Q}: single quotes, or $'...' when the text holds control characters. */
export function quoteValue(s: string): string {
  // eslint-disable-next-line no-control-regex
  return /[\x00-\x1f\x7f]/.test(s) ? shellQuote(s) : "'" + s.replaceAll("'", "'\\''") + "'";
}

/** Parses a printf integer argument like bash: 0x1f, 017 (octal), 'c (character code). */
export function cInteger(s: string): { value: bigint; valid: boolean } {
  const t = s.trim();
  if (t.length > 0 && (t[0] === "'" || t[0] === '"')) return { value: BigInt(t.length > 1 ? t.codePointAt(1)! : 0), valid: true };
  const m = /^[+-]?(0[xX][0-9a-fA-F]+|0[0-7]*|[1-9][0-9]*)/.exec(t);
  if (!m) return { value: 0n, valid: t.length === 0 };
  const body = m[1];
  let v = /^0x/i.test(body) ? BigInt('0x' + body.slice(2)) : body.startsWith('0') ? (body.length > 1 ? BigInt('0o' + body.slice(1)) : 0n) : BigInt(body);
  if (v > 0x7fffffffffffffffn) return { value: 0x7fffffffffffffffn, valid: false };
  if (t.startsWith('-')) v = -v;
  return { value: v, valid: m[0].length === t.length };
}

function cFloat(s: string): { value: number; valid: boolean } {
  const t = s.trim();
  if (t.length > 0 && (t[0] === "'" || t[0] === '"')) return { value: t.length > 1 ? t.codePointAt(1)! : 0, valid: true };
  const m = /^[+-]?(0[xX][0-9a-fA-F]+|\d+\.?\d*([eE][+-]?\d+)?|\.\d+([eE][+-]?\d+)?|inf(inity)?|nan)/i.exec(t);
  if (!m) return { value: 0, valid: t.length === 0 };
  const text = m[0].toLowerCase();
  const v = text.includes('inf') ? (text.startsWith('-') ? -Infinity : Infinity) : text.includes('nan') ? NaN
    : /0x/.test(text) ? Number(BigInt(text.replace(/^[+-]/, ''))) * (text.startsWith('-') ? -1 : 1) : Number(m[0]);
  return { value: v, valid: m[0].length === t.length };
}

function exponent(mag: number, prec: number, upper: boolean): string {
  const s = mag.toExponential(Math.min(prec, 100)); // 1.2345e+3
  const [mant, exp] = s.split('e');
  const sign = exp[0];
  const digits = exp.slice(1).padStart(2, '0');
  return mant + (upper ? 'E' : 'e') + sign + digits;
}

function fixed(mag: number, prec: number): string {
  if (mag >= 1e21) { // toFixed switches to exponent form; build the digits from BigInt
    const int = BigInt(Math.round(mag)).toString();
    return prec > 0 ? int + '.' + '0'.repeat(prec) : int;
  }
  return mag.toFixed(Math.min(prec, 100)) + (prec > 100 ? '0'.repeat(prec - 100) : '');
}

function general(mag: number, prec: number, upper: boolean, alt: boolean): string {
  const p = prec === 0 ? 1 : prec;
  if (mag === 0) return alt ? '0.' + '0'.repeat(p - 1) : '0';
  const e = exponent(mag, p - 1, upper);
  const x = parseInt(e.slice(e.search(/[eE]/) + 1), 10);
  const body = p > x && x >= -4 ? fixed(mag, p - 1 - x) : e;
  if (alt) return body;
  const ePos = body.search(/[eE]/);
  let mant = ePos < 0 ? body : body.slice(0, ePos);
  const rest = ePos < 0 ? '' : body.slice(ePos);
  if (mant.includes('.')) mant = mant.replace(/0+$/, '').replace(/\.$/, '');
  return mant + rest;
}

/** C printf formatting of one number (flags "-0 +#", width, precision; conversions d o x X e E f g G). */
export function formatNumber(flags: string, width: number, prec: number | null, conv: string, fval: number, ival: bigint): string {
  const left = flags.includes('-'), alt = flags.includes('#');
  let zero = flags.includes('0') && !left;
  let negative: boolean;
  let body: string;
  if ('doxXu'.includes(conv)) {
    negative = ival < 0n && conv === 'd';
    let mag = ival < 0n ? (conv === 'd' ? -ival : BigInt.asUintN(64, ival)) : ival;
    if (conv === 'u' && ival < 0n) { mag = BigInt.asUintN(64, ival); negative = false; }
    body = conv === 'o' ? mag.toString(8) : conv === 'x' ? mag.toString(16) : conv === 'X' ? mag.toString(16).toUpperCase() : mag.toString();
    if (prec !== null) { body = prec === 0 && mag === 0n ? '' : body.padStart(prec, '0'); zero = false; }
    if (alt && conv === 'o' && !body.startsWith('0')) body = '0' + body;
    if (alt && (conv === 'x' || conv === 'X') && mag !== 0n) body = (conv === 'x' ? '0x' : '0X') + body;
  } else {
    negative = fval < 0 || Object.is(fval, -0);
    const mag = Math.abs(fval);
    const pr = prec ?? 6;
    const upper = conv === conv.toUpperCase();
    if (Number.isNaN(fval)) { body = upper ? 'NAN' : 'nan'; negative = false; }
    else if (!Number.isFinite(fval)) body = upper ? 'INF' : 'inf';
    else {
      const lc = conv.toLowerCase();
      body = lc === 'f' ? fixed(mag, pr) : lc === 'e' ? exponent(mag, pr, upper) : general(mag, pr, upper, alt);
      if (alt && lc === 'f' && pr === 0) body += '.';
    }
    if (!Number.isFinite(fval)) zero = false;
  }
  const sign = negative ? '-' : flags.includes('+') ? '+' : flags.includes(' ') ? ' ' : '';
  const pad = width - sign.length - body.length;
  if (pad <= 0) return sign + body;
  if (left) return sign + body + ' '.repeat(pad);
  if (zero) {
    const prefix = /^0[xX]/.test(body) ? body.slice(0, 2) : '';
    return sign + prefix + '0'.repeat(pad) + body.slice(prefix.length);
  }
  return ' '.repeat(pad) + sign + body;
}

const SPEC = /%([-0 +#']*)(\*|\d*)(?:\.(\*|\d*))?(?:\(([^)]*)\))?([sdiufxXoceEgGbqTaA%])|%/g;
const STOP = '';

/** bash printf: C conversions; the format is reused while arguments remain; bad numbers are reported in `errors`. */
export function printfFormat(format: string, args: string[], errors?: string[], now: () => number = Date.now): string {
  const fmt = unescape(format, true);
  let out = '';
  let next = 0;
  let stop = false; // a \c in a %b argument ends all output, the rest of the format included
  const take = () => (next < args.length ? args[next++] : (next++, ''));
  do {
    const before = next;
    out += fmt.replace(SPEC, (all, flags: string | undefined, w: string | undefined, p: string | undefined, tfmt: string | undefined, conv: string | undefined) => {
      if (conv === undefined) return all; // a lone % at the end
      if (conv === '%') return '%';
      if ((tfmt !== undefined) !== (conv === 'T')) return all;
      flags ??= '';
      let width = 0;
      if (w === '*') {
        let v = Number(cInteger(take()).value);
        if (v < 0) { flags += '-'; v = -v; }
        width = Math.min(v, 10_000);
      } else if (w) width = Math.min(Number(w), 10_000);
      let prec: number | null = null;
      if (p !== undefined) prec = p === '*' ? Math.max(0, Math.min(Number(cInteger(take()).value), 10_000)) : p ? Math.min(Number(p), 10_000) : 0;
      const arg = take();
      if ('sbqcT'.includes(conv)) {
        if (stop) return '';
        let text: string;
        if (conv === 'b') {
          const cut = arg.indexOf('\\c');
          if (cut >= 0) { stop = true; return unescape(arg.slice(0, cut), true, true) + STOP; }
          text = unescape(arg, true, true);
        } else if (conv === 'q') text = shellQuote(arg);
        else if (conv === 'c') text = arg.length > 0 ? [...arg][0] : '';
        else if (conv === 'T') text = strftime(arg === '' || arg === '-1' || arg === '-2' ? now() : Number(cInteger(arg).value) * 1000, tfmt!);
        else text = arg;
        if (prec !== null && prec < text.length && conv !== 'c') text = text.slice(0, prec);
        return flags.includes('-') ? text.padEnd(width) : text.padStart(width);
      }
      if ('dioxXu'.includes(conv)) {
        const { value, valid } = cInteger(arg);
        if (!valid) errors?.push(`printf: ${arg}: invalid number`);
        return formatNumber(flags, width, prec, conv === 'i' ? 'd' : conv, 0, value);
      }
      const { value, valid } = cFloat(arg);
      if (!valid) errors?.push(`printf: ${arg}: invalid number`);
      return formatNumber(flags, width, prec, conv === 'a' || conv === 'A' ? 'e' : conv, value, 0n);
    });
    if (stop) { out = out.slice(0, out.indexOf(STOP)); break; }
    if (next === before) break; // the format consumed no arguments
  } while (next < args.length);
  return out;
}

const DAYS = ['Sunday', 'Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday'];
const MONTHS = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December'];

/** strftime in UTC (the machine's one time zone). `ms` is milliseconds since the epoch. */
export function strftime(ms: number, fmt: string): string {
  const d = new Date(ms);
  const Y = d.getUTCFullYear(), m = d.getUTCMonth(), day = d.getUTCDate(), H = d.getUTCHours(), M = d.getUTCMinutes(), S = d.getUTCSeconds();
  const wd = d.getUTCDay();
  const p2 = (n: number) => String(n).padStart(2, '0');
  const yday = Math.floor((Date.UTC(Y, m, day) - Date.UTC(Y, 0, 1)) / 86_400_000) + 1;
  const isoWeek = () => {
    const t = new Date(Date.UTC(Y, m, day));
    const dn = (wd + 6) % 7;
    t.setUTCDate(t.getUTCDate() - dn + 3);
    const firstThu = new Date(Date.UTC(t.getUTCFullYear(), 0, 4));
    return { year: t.getUTCFullYear(), week: 1 + Math.round(((t.getTime() - firstThu.getTime()) / 86_400_000 - 3 + ((firstThu.getUTCDay() + 6) % 7)) / 7) };
  };
  let out = '';
  for (let i = 0; i < fmt.length; i++) {
    if (fmt[i] !== '%' || i + 1 >= fmt.length) { out += fmt[i]; continue; }
    let c = fmt[++i];
    let pad: string | null = null;
    if (c === '-' || c === '_' || c === '0' || c === '^') { pad = c; c = fmt[++i] ?? ''; }
    let v: string;
    switch (c) {
      case 'Y': v = String(Y); break;
      case 'C': v = p2(Math.floor(Y / 100)); break;
      case 'y': v = p2(Y % 100); break;
      case 'm': v = p2(m + 1); break;
      case 'd': v = p2(day); break;
      case 'e': v = String(day).padStart(2, ' '); break;
      case 'H': v = p2(H); break;
      case 'I': v = p2(H % 12 === 0 ? 12 : H % 12); break;
      case 'k': v = String(H).padStart(2, ' '); break;
      case 'l': v = String(H % 12 === 0 ? 12 : H % 12).padStart(2, ' '); break;
      case 'M': v = p2(M); break;
      case 'S': v = p2(S); break;
      case 'N': v = String((ms % 1000 + 1000) % 1000).padStart(3, '0') + '000000'; break;
      case 'p': v = H < 12 ? 'AM' : 'PM'; break;
      case 'P': v = H < 12 ? 'am' : 'pm'; break;
      case 'A': v = DAYS[wd]; break;
      case 'a': v = DAYS[wd].slice(0, 3); break;
      case 'B': v = MONTHS[m]; break;
      case 'b': case 'h': v = MONTHS[m].slice(0, 3); break;
      case 'j': v = String(yday).padStart(3, '0'); break;
      case 'u': v = String(wd === 0 ? 7 : wd); break;
      case 'w': v = String(wd); break;
      case 'U': v = p2(Math.floor((yday + 6 - wd) / 7)); break;
      case 'W': v = p2(Math.floor((yday + 6 - ((wd + 6) % 7)) / 7)); break;
      case 'V': v = p2(isoWeek().week); break;
      case 'G': v = String(isoWeek().year); break;
      case 'g': v = p2(isoWeek().year % 100); break;
      case 's': v = String(Math.floor(ms / 1000)); break;
      case 'Z': v = 'UTC'; break;
      case 'z': v = '+0000'; break;
      case ':': { // %:z +hh:mm, %::z +hh:mm:ss, %:::z +hh (the clock is UTC)
        let k = 1;
        while (fmt[i + k] === ':') k++;
        if (k <= 3 && fmt[i + k] === 'z') { i += k; v = k === 1 ? '+00:00' : k === 2 ? '+00:00:00' : '+00'; } else v = '%:';
        break;
      }
      case 'F': v = `${Y}-${p2(m + 1)}-${p2(day)}`; break;
      case 'T': v = `${p2(H)}:${p2(M)}:${p2(S)}`; break;
      case 'R': v = `${p2(H)}:${p2(M)}`; break;
      case 'D': case 'x': v = `${p2(m + 1)}/${p2(day)}/${p2(Y % 100)}`; break;
      case 'X': v = `${p2(H)}:${p2(M)}:${p2(S)}`; break;
      case 'r': v = `${p2(H % 12 === 0 ? 12 : H % 12)}:${p2(M)}:${p2(S)} ${H < 12 ? 'AM' : 'PM'}`; break;
      case 'c': v = `${DAYS[wd].slice(0, 3)} ${MONTHS[m].slice(0, 3)} ${String(day).padStart(2, ' ')} ${p2(H)}:${p2(M)}:${p2(S)} ${Y}`; break;
      case 'n': v = '\n'; break;
      case 't': v = '\t'; break;
      case '%': v = '%'; break;
      default: v = '%' + (pad ?? '') + c;
    }
    if (pad === '-') v = v.replace(/^[0 ]+(?=.)/, '');
    else if (pad === '_') v = v.replace(/^0+(?=.)/, m0 => ' '.repeat(m0.length));
    else if (pad === '0') v = v.replace(/^ +/, m0 => '0'.repeat(m0.length));
    else if (pad === '^') v = v.toUpperCase();
    out += v;
  }
  return out;
}
