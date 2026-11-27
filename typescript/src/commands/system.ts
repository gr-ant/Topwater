// System identity and packages: whoami, hostname, uname, date, sudo, apt, dpkg, su, id, uptime, free, nproc. See docs/architecture.md.
import type { Shell } from '../shell.js';
import { Args, specOf, specMessage, ShellError, UsageException, line, type CommandHandler } from '../command.js';
import { strftime } from '../format.js';

const ISO_UNITS = ['hours', 'minutes', 'date', 'seconds', 'ns'];

const ISO_FORMATS = ['%Y-%m-%dT%H%:z', '%Y-%m-%dT%H:%M%:z', '%Y-%m-%d', '%Y-%m-%dT%H:%M:%S%:z', '%Y-%m-%dT%H:%M:%S,%N%:z'];

const MONTH_ABBR = ['jan', 'feb', 'mar', 'apr', 'may', 'jun', 'jul', 'aug', 'sep', 'oct', 'nov', 'dec'];

const DAY_ABBR = ['sun', 'mon', 'tue', 'wed', 'thu', 'fri', 'sat'];

const ZONE_HOURS: Record<string, number> = { utc: 0, gmt: 0, ut: 0, z: 0, est: -5, edt: -4, cst: -6, cdt: -5, mst: -7, mdt: -6, pst: -8, pdt: -7 };

const DATE_UNITS: Record<string, number> = { second: 1, sec: 1, minute: 60, min: 60, hour: 3600, day: 86400, week: 604800, fortnight: 1209600 };

/**
 * date -d, from GNU date's documented input formats: items in any order, each optional. Calendar dates
 * (2026-11-20, 11/20/2026, 20 Nov 2026, Nov 20, 2026, 20261120), times (13:30[:00[.5]], 1pm), zones
 * (UTC, Z, EST..., +0100, +01:00), a day of the week, today/now/yesterday/tomorrow, @EPOCH, and relative items
 * ("3 days ago", "next week", "+2 hours") applied last. Results are UTC (ms since the epoch).
 */
export function parseDate(text: string, now: number): number {
  const bad = new ShellError(specMessage('date', 'invalidDate', { value: text }));
  let s = text.trim().toLowerCase();
  const epoch = /^@(-?\d+(?:\.\d+)?)$/.exec(s);
  if (epoch) return Number(epoch[1]) * 1000;
  let year: number | undefined, month: number | undefined, day: number | undefined, zone: number | undefined;
  let time: number | undefined; // ms into the day
  let weekday: number | undefined, dayShift: number | undefined;
  const relative: [number, string][] = [];
  const MONTHS = '(jan|feb|mar|apr|may|jun|jul|aug|sep|oct|nov|dec)[a-z]*\\.?';
  let m: RegExpExecArray | null = null;
  const eat = (pattern: string) => {
    m = new RegExp('^(?:' + pattern + ')').exec(s);
    if (!m) return false;
    s = s.slice(m[0].length).replace(/^[ \t,]+/, '');
    return true;
  };
  const g = (i: number) => Number(m![i]);
  const has = (i: number) => m![i] !== undefined;
  const monthOf = (i: number) => MONTH_ABBR.indexOf(m![i].slice(0, 3)) + 1;
  const fullYear = (y: number, digits: number) => (digits > 2 ? y : y < 69 ? 2000 + y : 1900 + y);
  const setDate = (y: number | undefined, mo: number, d: number) => { if (day !== undefined) throw bad; year = y ?? year; month = mo; day = d; };
  const setZone = (minutes: number) => { if (zone !== undefined) throw bad; zone = minutes; };
  let afterTime = false;
  while (s.length > 0) {
    // a signed number right after a time of day is its zone ("10:00 +2" is UTC+2), anywhere else it's relative
    if (afterTime && eat('([+-])\\s*(\\d{1,2}:\\d{2}|\\d{1,4})\\b')) {
      afterTime = false;
      const v = m![2].replace(':', '');
      const minutes = v.length <= 2 ? Number(v) * 60 : Number(v.slice(0, -2)) * 60 + Number(v.slice(-2));
      setZone(m![1] === '-' ? -minutes : minutes);
      continue;
    }
    let wasTime = false;
    if (eat('(\\d{4})-(\\d{1,2})-(\\d{1,2})(?:t(?=\\d))?')) setDate(g(1), g(2), g(3));
    else if (eat('(\\d{1,2})/(\\d{1,2})(?:/(\\d{2}|\\d{4}))?\\b')) setDate(has(3) ? fullYear(g(3), m![3].length) : undefined, g(1), g(2));
    else if (eat('(\\d{4})(\\d{2})(\\d{2})\\b')) setDate(g(1), g(2), g(3));
    else if (eat('(\\d{1,2}):(\\d{2})(?::(\\d{2})(?:[.,](\\d+))?)?\\s*(?:(a|p)\\.?m\\.?\\b)?')) {
      if (time !== undefined) throw bad;
      let h = g(1);
      if (has(5)) { if (h < 1 || h > 12) throw bad; h = (h % 12) + (m![5] === 'p' ? 12 : 0); }
      const frac = has(4) ? Number('0.' + m![4]) : 0;
      if (h > 23 || g(2) > 59 || (has(3) && g(3) > 60)) throw bad;
      time = ((h * 60 + g(2)) * 60 + (has(3) ? g(3) : 0) + frac) * 1000;
      wasTime = true;
    } else if (eat('(\\d{1,2})\\s*(a|p)\\.?m\\.?\\b')) {
      if (time !== undefined || g(1) < 1 || g(1) > 12) throw bad;
      time = ((g(1) % 12) + (m![2] === 'p' ? 12 : 0)) * 3_600_000;
      wasTime = true;
    } else if (eat('(\\d{1,2})(?:st|nd|rd|th)?[\\s-]*' + MONTHS + '(?:[\\s-]+(\\d{4})\\b)?')) setDate(has(3) ? g(3) : undefined, monthOf(2), g(1));
    else if (eat(MONTHS + '\\s*(\\d{1,2})(?:st|nd|rd|th)?\\b(?:,?\\s*(\\d{4})\\b)?')) setDate(has(3) ? g(3) : undefined, monthOf(1), g(2));
    else if (eat('(next|last|this|[+-]?\\s*\\d+)?\\s*(second|sec|minute|min|hour|day|week|fortnight|month|year)s?\\b(\\s+ago\\b)?')) {
      const word = has(1) ? m![1].replace(/\s/g, '') : '1';
      const n = word === 'next' ? 1 : word === 'last' ? -1 : word === 'this' ? 0 : Number(word);
      relative.push([has(3) ? -n : n, m![2]]);
    } else if (eat('(utc|gmt|ut|z|[ecmp][sd]t)\\b(?:([+-])(\\d{1,2})(?::?(\\d{2}))?)?')) {
      let offset = ZONE_HOURS[m![1]] * 60;
      if (has(2)) offset += (m![2] === '-' ? -1 : 1) * (g(3) * 60 + (has(4) ? g(4) : 0));
      setZone(offset);
    } else if (eat('([+-])(\\d{2}):?(\\d{2})\\b')) setZone((m![1] === '-' ? -1 : 1) * (g(2) * 60 + g(3)));
    else if (eat('(sun|mon|tue|wed|thu|fri|sat)[a-z]*\\.?\\b')) {
      if (weekday !== undefined) throw bad;
      weekday = DAY_ABBR.indexOf(m![1]);
    } else if (eat('(today|now|yesterday|tomorrow)\\b')) {
      if (dayShift !== undefined) throw bad;
      dayShift = m![1] === 'yesterday' ? -1 : m![1] === 'tomorrow' ? 1 : 0;
    } else if (year === undefined && day !== undefined && eat('(\\d{4})\\b')) year = g(1); // "Fri Nov 20 13:30:00 UTC 2026"
    else throw bad;
    afterTime = wasTime;
  }
  // absolute items set the date and time; anything not given comes from now, and a date alone means midnight
  const absolute = day !== undefined || time !== undefined || weekday !== undefined;
  const n = new Date(now);
  const y = year ?? n.getUTCFullYear(), mo = month ?? n.getUTCMonth() + 1, d = day ?? n.getUTCDate();
  if (mo < 1 || mo > 12 || d < 1 || d > new Date(Date.UTC(y, mo, 0)).getUTCDate()) throw bad;
  let t = Date.UTC(y, mo - 1, d);
  t += time ?? (absolute ? 0 : now - Date.UTC(n.getUTCFullYear(), n.getUTCMonth(), n.getUTCDate()));
  if (zone !== undefined) t -= zone * 60_000;
  if (weekday !== undefined) t += ((((weekday - new Date(t).getUTCDay()) % 7) + 7) % 7) * 86_400_000;
  if (dayShift !== undefined) t += dayShift * 86_400_000;
  for (const [count, unit] of relative) {
    if (unit === 'month' || unit === 'year') {
      // like GNU date, a day past the end of the month rolls over (Jan 31 + 1 month = Mar 2 or 3)
      const dt = new Date(t);
      const months = dt.getUTCMonth() + count * (unit === 'year' ? 12 : 1);
      t = Date.UTC(dt.getUTCFullYear(), months, 1, dt.getUTCHours(), dt.getUTCMinutes(), dt.getUTCSeconds(), dt.getUTCMilliseconds()) + (dt.getUTCDate() - 1) * 86_400_000;
    } else t += count * DATE_UNITS[unit] * 1000;
  }
  return t;
}

export function registerSystem(shell: Shell) {
  const reg = (name: string, handler: CommandHandler) => shell.register(name, handler);

  reg('whoami', ctx => line(ctx, ctx.shell.options.user));
  reg('hostname', ctx => line(ctx, ctx.shell.options.hostname));
  reg('uname', ctx => {
    const a = Args.parse(ctx.args, specOf('uname'));
    const all = a.has('a');
    const parts: string[] = [];
    if (all || a.has('s') || a.flags.size === 0) parts.push('Linux');
    if (all || a.has('n')) parts.push(ctx.shell.options.hostname);
    if (all || a.has('r')) parts.push('6.8.0-generic');
    if (all || a.has('v')) parts.push('#1 SMP PREEMPT_DYNAMIC');
    if (all || a.has('m')) parts.push('x86_64');
    if (a.has('p') && !all) parts.push('x86_64');
    if (a.has('i') && !all) parts.push('x86_64');
    if (all || a.has('o')) parts.push('GNU/Linux');
    line(ctx, parts.join(' '));
  });

  reg('date', ctx => {
    // the simulated machine's clock is UTC (TZ is unset), so output never depends on the host's time zone
    // -I[UNIT] / --iso-8601[=UNIT] take an optional value, so they are read here, before the shared parser
    let iso: string | undefined;
    const args = ctx.args.filter(x => {
      const unit = x === '--iso-8601' ? '' : x.startsWith('--iso-8601=') ? x.slice(11) : x.startsWith('-I') ? x.slice(2) : undefined;
      if (unit === undefined) return true;
      const found = unit === '' ? 'date' : ISO_UNITS.find(u => u.startsWith(unit));
      if (found === undefined) throw new UsageException(specMessage('date', 'invalidIso', { value: unit }));
      iso = found;
      return false;
    });
    const a = Args.parse(args, specOf('date'));
    let now = ctx.shell.now();
    const d = a.value('d'), r = a.value('r');
    if (d !== undefined) now = parseDate(d, now);
    else if (r !== undefined) now = ctx.fs.stat(ctx.resolvePath(r)).modified;
    const fmt = a.positional.find(p => p.startsWith('+'));
    const extra = a.positional.find(p => !p.startsWith('+'));
    if (extra !== undefined) throw new ShellError(specMessage('date', 'invalidDate', { value: extra }));
    line(ctx, strftime(now, iso !== undefined ? ISO_FORMATS[ISO_UNITS.indexOf(iso)] : a.has('R') ? '%a, %d %b %Y %H:%M:%S +0000' : fmt !== undefined ? fmt.slice(1) : '%a %b %e %H:%M:%S %Z %Y'));
  });

  reg('clear', () => 0);

  // the agent is a regular user: privilege escalation fails the way it does on a real server
  reg('sudo', ctx => {
    const user = ctx.shell.options.user;
    if (ctx.args.length === 1 && (ctx.args[0] === '-V' || ctx.args[0] === '--version')) { line(ctx, 'Sudo version 1.9.15p5'); return 0; }
    if (ctx.args.length === 0) { ctx.stderr.write(specMessage('sudo', 'usage') + '\n'); return 1; }
    if (ctx.args[0] === '-l' || ctx.args[0] === '--list') { ctx.stderr.write(specMessage('sudo', 'listDenied', { user: user, host: ctx.shell.options.hostname }) + '\n'); return 1; }
    // `sudo apt install x`: the package manager's own answer (nothing can be installed) says more than a refusal
    const k = ctx.args.findIndex(a => !a.startsWith('-'));
    if (k >= 0 && ['apt', 'apt-get', 'apt-cache', 'dpkg'].includes(ctx.args[k])) return ctx.invoke(ctx.args.slice(k));
    ctx.stderr.write(specMessage('sudo', 'notSudoer', { user: user }) + '\n');
    return 1;
  });

  // no package sources and no network: package managers say so, with their usual failure status
  for (const name of ['apt', 'apt-get', 'apt-cache']) reg(name, ctx => { ctx.stderr.write(specMessage(name, 'unavailable') + '\n'); return 100; });
  reg('dpkg', ctx => { ctx.stderr.write('dpkg: ' + specMessage('dpkg', 'unavailable') + '\n'); return 2; });
  reg('su', ctx => { ctx.stderr.write(specMessage('su', 'authenticationFailure') + '\n'); return 1; });

  reg('id', ctx => {
    const u = ctx.shell.options.user;
    const a = Args.parse(ctx.args, specOf('id'));
    line(ctx, a.has('u') || a.has('g') || a.has('G') ? (a.has('n') ? u : '1000') : `uid=1000(${u}) gid=1000(${u}) groups=1000(${u})`);
  });
  reg('uptime', ctx => line(ctx, ctx.args.includes('-p') ? 'up 3 days, 4 hours, 12 minutes'
    : ` ${strftime(ctx.shell.now(), '%H:%M:%S')} up 3 days,  4:12,  1 user,  load average: 0.08, 0.03, 0.01`));
  reg('free', ctx => {
    const a = Args.parse(ctx.args, specOf('free'));
    const binary = 'bkmg\u0001\u0002', decimal = '_\u0003\u0004\u0005\u0006\u0007'; // option keys by power: --tebi is \u0001, --kilo \u0003
    const units = [...binary + decimal.slice(1)].filter(c => a.has(c));
    // shortcut: a repeated flag (-m -m) is counted once, real free rejects it too
    if (units.length > 1) throw new ShellError(specMessage('free', 'multipleUnits'));
    const unit = units[0] ?? 'k';
    const si = a.has('\u000e') || decimal.includes(unit), base = si ? 1000 : 1024, wide = a.has('w');
    const power = binary.includes(unit) ? binary.indexOf(unit) : decimal.indexOf(unit);
    const size = (kb: number): string => {
      const n = kb * 1024;
      if (!a.has('h')) return String(Math.floor(n / base ** power));
      if (n < base) return `${n}B`;
      for (let i = 1; ; i++) {
        const v = n / base ** i, suffix = 'KMGTPE'[i - 1] + (si ? '' : 'i');
        if (v < 10) return v.toFixed(1) + suffix;
        if (v < base || i === 6) return Math.trunc(v) + suffix;
      }
    };
    const row = (label: string, cells: string[]) => label.padEnd(8) + cells.map(c => c.padStart(12)).join('') + '\n';
    const mem = [8014880, 1992344, 3984512, 12288, 180224, 2020800, 5748660], swap = [2097148, 0, 2097148]; // kB: total used free shared buffers cache available
    const shown = wide ? mem : [mem[0], mem[1], mem[2], mem[3], mem[4] + mem[5], mem[6]];
    ctx.stdout.write(row('', wide ? ['total', 'used', 'free', 'shared', 'buffers', 'cache', 'available'] : ['total', 'used', 'free', 'shared', 'buff/cache', 'available'])
      + row('Mem:', shown.map(size)) + row('Swap:', swap.map(size))
      + (a.has('t') ? row('Total:', swap.map((s, i) => size(mem[i] + s))) : ''));
  });
  reg('nproc', ctx => line(ctx, '4'));
}
