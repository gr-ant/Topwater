// Variable built-ins: export, unset, env, alias, declare and its spellings, read, mapfile, printenv. See docs/architecture.md.
import type { Shell } from '../shell.js';
import { DIAGNOSTICS, ERRNO, GETOPT } from '../spec.generated.js';
import { Args, specFormat, specOf, specMessage, ShellError, UsageException, StdinBuffer, ioOf, line, cannotRun, type CommandContext, type CommandHandler } from '../command.js';
import { isName } from '../parser.js';
import { shellQuote } from '../format.js';
import { ordinal } from '../vfs.js';
import { splitFields } from '../shell/expansion.js';

/**
 * `read a b c`: the fields for each name. The last name gets the rest of the line less trailing IFS whitespace,
 * and a single trailing IFS delimiter is dropped when the rest is just one field ("x,y," gives b=y, as in bash).
 */
function readFields(text: string, ifs: string, count: number): string[] {
  const ws = [...' \t\n'].filter(c => ifs.includes(c));
  const isWs = (c: string) => ws.includes(c);
  const fields: string[] = [];
  let i = 0;
  while (i < text.length && isWs(text[i])) i++;
  while (fields.length < count - 1 && i < text.length) {
    const start = i;
    while (i < text.length && !ifs.includes(text[i])) i++;
    fields.push(text.slice(start, i));
    while (i < text.length && isWs(text[i])) i++;
    if (i < text.length && ifs.includes(text[i]) && !isWs(text[i])) { i++; while (i < text.length && isWs(text[i])) i++; }
  }
  const trimWs = (t: string) => { let e = t.length; while (e > 0 && isWs(t[e - 1])) e--; return t.slice(0, e); };
  let rest = trimWs(text.slice(i));
  if (rest.length > 0 && ifs.includes(rest[rest.length - 1]) && !isWs(rest[rest.length - 1]) && ![...trimWs(rest.slice(0, -1))].some(c => ifs.includes(c)))
    rest = trimWs(rest.slice(0, -1));
  if (i < text.length || fields.length < count) fields.push(rest);
  return fields;
}

/** A variable's attribute letters in bash's order (a A i n r t x l u). */
export function attributeFlags(sh: Shell, name: string): string {
  const attrs = sh.varAttrs.get(name) ?? '';
  const array = sh.arrays.get(name);
  return [...'aAinrtxlu'].filter(c => (c === 'a' ? array !== undefined && !array.assoc : c === 'A' ? array?.assoc === true : c === 'x' ? sh.isExported(name) : attrs.includes(c))).join('');
}

/** `declare -p NAME`: a line that recreates the variable when evaluated; null when there is no such variable. */
export function declareLine(sh: Shell, name: string): string | null {
  const flags = attributeFlags(sh, name);
  const head = `declare -${flags || '-'} ${name}`;
  // eslint-disable-next-line no-control-regex
  const q = (v: string) => (/[\x00-\x1f\x7f]/.test(v) ? shellQuote(v) : '"' + v.replace(/[\\"$`]/g, '\\$&') + '"');
  const a = sh.arrays.get(name);
  if (a) {
    if (a.size === 0) return head + '=()';
    const items = a.keys().map(k => `[${a.assoc && !/^[A-Za-z0-9_]+$/.test(k) ? q(k) : k}]=${q(a.get(k)!)}`);
    return head + '=(' + (a.assoc ? items.map(i => i + ' ').join('') : items.join(' ')) + ')';
  }
  const value = sh.environment.get(name);
  return value !== undefined ? head + '=' + q(value) : flags ? head : null;
}

export function allVariableNames(sh: Shell): string[] {
  return [...new Set([...sh.environment.keys(), ...sh.arrays.keys()])].sort(ordinal);
}

function inputFor(ctx: CommandContext, u: string | undefined): StdinBuffer {
  if (u === undefined || u === '0') return ctx.stdin;
  const fd = /^\d+$/.test(u) ? Number(u) : -1;
  const f = ctx.fds?.get(fd);
  const input = f instanceof StdinBuffer ? f : ctx.shell.inputFd(fd);
  if (!input) throw new ShellError(specMessage(ctx.name, 'badFileDescriptor', { fd: u }));
  return input;
}

export function registerVariables(shell: Shell) {
  const reg = (name: string, handler: CommandHandler) => shell.register(name, handler);

  reg('export', ctx => {
    const sh = ctx.shell;
    let k = 0;
    while (k < ctx.args.length && ctx.args[k].startsWith('-')) k++;
    const flags = ctx.args.slice(0, k).join('');
    const args = ctx.args.slice(k);
    const remove = flags.includes('n');
    if (flags.includes('f')) {
      // export -f: functions a child bash also gets
      for (const f of args) if (remove) sh.exportedFunctions.delete(f); else if (sh.functions.has(f)) sh.exportedFunctions.add(f);
      return 0;
    }
    if (args.length === 0) {
      for (const n of allVariableNames(sh)) if (sh.isExported(n)) { const l = declareLine(sh, n); if (l) line(ctx, l); }
      return 0;
    }
    let code = 0;
    for (const a of args) {
      const eq = a.indexOf('=');
      const append = eq > 0 && a[eq - 1] === '+';
      const name = eq > 0 ? a.slice(0, append ? eq - 1 : eq) : a;
      try {
        if (!isName(name)) throw new ShellError(specFormat(DIAGNOSTICS.notValidIdentifier, { name: a }));
        const target = sh.resolve(name);
        if (eq > 0) sh.setVar(name, a.slice(eq + 1), append);
        else if (!sh.arrays.has(target) && !sh.environment.has(target)) { sh.markShellVariable(target); sh.environment.set(target, ''); }
        if (remove) sh.unexport(target); else sh.setAttrs(target, 'x');
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        ctx.stderr.write(`${ctx.shell.errPrefix}export: ${e.message}\n`);
        code = 1;
      }
    }
    return code;
  });

  reg('unset', async ctx => {
    const sh = ctx.shell;
    let k = 0;
    while (k < ctx.args.length && ctx.args[k].startsWith('-')) k++;
    const flags = ctx.args.slice(0, k).join('');
    let code = 0;
    for (const arg of ctx.args.slice(k)) {
      try {
        if (!flags.includes('f') && !isName(arg.split('[')[0])) {
          if (flags.includes('v')) throw new ShellError(specFormat(DIAGNOSTICS.notValidIdentifier, { name: arg }));
          continue; // plain unset passes over what can be neither a variable nor a function, silently
        }
        const a = await sh.expandSubscript(arg, ioOf(ctx));
        // with neither -v nor -f, a name that is no variable but a function unsets the function
        if (flags.includes('f') || (!flags.includes('v') && !sh.isVariableSet(a) && sh.functions.has(a))) sh.functions.delete(a);
        else sh.unset(a, flags.includes('n'));
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        ctx.stderr.write(`${ctx.shell.errPrefix}unset: ${e.message}\n`);
        code = 1;
      }
    }
    return code;
  });

  reg('env', async ctx => {
    // env [-i] [-u NAME] [-C DIR] [-0] [NAME=VALUE]... [COMMAND [ARG]...]; options end at the first other word
    const args = [...ctx.args];
    const sets: [string, string][] = [];
    const unsets: string[] = [];
    let clear = false, nul = false, dir: string | undefined;
    const option = (c: string, value: () => string) => {
      if (c === 'i') clear = true;
      else if (c === '0') nul = true;
      else if (c === 'u') unsets.push(value());
      else dir = value();
    };
    const LONG: Record<string, string> = { 'ignore-environment': 'i', unset: 'u', chdir: 'C', null: '0' };
    let options = true; // after --, only NAME=VALUE words
    while (args.length) {
      const a = args[0];
      if (options && a === '-') { clear = true; args.shift(); continue; }
      if (options && a === '--') { args.shift(); options = false; continue; }
      if (options && a.startsWith('--')) {
        args.shift();
        const eq = a.indexOf('='), name = eq < 0 ? a.slice(2) : a.slice(2, eq);
        const c = LONG[name];
        if (c === undefined) throw new UsageException(specFormat(GETOPT.unrecognizedOption, { option: a }));
        option(c, () => eq >= 0 ? a.slice(eq + 1) : args.length ? args.shift()! : (() => { throw new UsageException(specFormat(GETOPT.missingLongArgument, { option: name })); })());
        continue;
      }
      if (options && a.length > 1 && a[0] === '-') {
        args.shift();
        for (let k = 1; k < a.length; k++) {
          const c = a[k];
          if (!'i0uC'.includes(c)) throw new UsageException(specFormat(GETOPT.invalidOption, { option: c }));
          if (c === 'u' || c === 'C') {
            const rest = a.slice(k + 1);
            option(c, () => rest || (args.length ? args.shift()! : (() => { throw new UsageException(specFormat(GETOPT.missingShortArgument, { option: c })); })()));
            break;
          }
          option(c, () => '');
        }
        continue;
      }
      const eq = a.indexOf('=');
      if (eq > 0) { sets.push([a.slice(0, eq), a.slice(eq + 1)]); args.shift(); continue; }
      break;
    }
    const print = () => {
      for (const k of [...ctx.shell.environment.keys()].sort(ordinal)) if (ctx.shell.isExported(k)) ctx.stdout.write(`${k}=${ctx.shell.environment.get(k)}${nul ? '\0' : '\n'}`);
    };
    if (args.length === 0 && sets.length === 0 && !clear && unsets.length === 0) { print(); return 0; }
    const sh = ctx.shell;
    const savedEnv = sh.environment, savedAttrs = sh.varAttrs, savedCwd = sh.cwd;
    sh.environment = new Map(clear ? [] : [...savedEnv].filter(([k]) => sh.isExported(k) && !unsets.includes(k)));
    sh.varAttrs = new Map([...sh.environment.keys()].map(k => [k, 'x']));
    for (const [k, v] of sets) { sh.environment.set(k, v); sh.varAttrs.set(k, 'x'); }
    try {
      if (dir !== undefined) {
        const abs = ctx.resolvePath(dir);
        if (!ctx.fs.isDirectory(abs)) throw new ShellError(specMessage('env', 'cannotChdir', { dir, errno: ctx.fs.exists(abs) ? ERRNO.ENOTDIR : ctx.fs.whyMissing(abs) }), 125);
        const pwd = sh.environment.get('PWD');
        sh.cwd = abs;
        if (pwd === undefined) sh.environment.delete('PWD'); else sh.environment.set('PWD', pwd); // env -C leaves PWD alone
      }
      if (args.length === 0) { print(); return 0; }
      // with no PATH, env searches /bin and /usr/bin, as execvp does
      if (!sh.environment.has('PATH') && !args[0].includes('/')) {
        const found = ['/bin/', '/usr/bin/'].find(d => ctx.fs.isFile(d + args[0]));
        if (found) args[0] = found + args[0];
      }
      // env runs programs: a name that is none is env's own error, not the shell's
      const bad = cannotRun(ctx, args[0]);
      if (bad) throw new ShellError(specMessage('env', 'runFailed', { command: args[0], errno: bad[0] }), bad[1]);
      return await sh.invoke(args, ioOf(ctx), true);
    } finally {
      if (sh.cwd !== savedCwd) try { sh.cwd = savedCwd; } catch { /* removed meanwhile */ }
      sh.environment = savedEnv; sh.varAttrs = savedAttrs;
    }
  });

  reg('alias', ctx => {
    const sh = ctx.shell;
    let code = 0;
    const args = ctx.args.filter(a => a !== '-p');
    if (args.length === 0) for (const k of [...sh.aliases.keys()].sort(ordinal)) line(ctx, `alias ${k}='${sh.aliases.get(k)}'`);
    for (const a of args) {
      const eq = a.indexOf('=');
      if (eq > 0) sh.aliases.set(a.slice(0, eq), a.slice(eq + 1));
      else if (sh.aliases.has(a)) line(ctx, `alias ${a}='${sh.aliases.get(a)}'`);
      else { ctx.stderr.write(`${ctx.shell.errPrefix}alias: ${specMessage('alias', 'notFound', { name: a })}\n`); code = 1; }
    }
    return code;
  });

  reg('unalias', ctx => {
    const usage = () => { ctx.stderr.write(`unalias: ${specMessage('unalias', 'usage')}\n`); return 2; };
    let i = 0;
    for (; i < ctx.args.length && ctx.args[i].length > 1 && ctx.args[i][0] === '-'; i++) {
      if (ctx.args[i] === '--') { i++; break; }
      const bad = [...ctx.args[i].slice(1)].find(c => c !== 'a');
      if (bad !== undefined) { ctx.stderr.write(`${ctx.shell.errPrefix}unalias: ${specMessage('unalias', 'builtinInvalidOption', { option: '-' + bad })}\n`); return usage(); }
      ctx.shell.aliases.clear();
      return 0;
    }
    if (i >= ctx.args.length) return usage();
    let code = 0;
    for (const a of ctx.args.slice(i)) if (!ctx.shell.aliases.delete(a)) { ctx.stderr.write(`${ctx.shell.errPrefix}unalias: ${specMessage('unalias', 'notFound', { name: a })}\n`); code = 1; }
    return code;
  });

  const declare: CommandHandler = ctx => {
    const sh = ctx.shell;
    let on = ctx.name === 'readonly' ? 'r' : '', off = '', listOnly = '';
    let print = false, functions = false, functionNames = false, global = false;
    const names: string[] = [];
    for (const a of ctx.args) {
      if (names.length > 0 || a.length < 2 || (a[0] !== '-' && a[0] !== '+') || a === '--') { if (a !== '--' || names.length > 0) names.push(a); continue; }
      for (const c of a.slice(1)) {
        switch (c) {
          case 'p': print = true; break;
          case 'f': functions = true; break;
          case 'F': functionNames = true; break;
          case 'g': global = true; break;
          case 'a': case 'A': listOnly += c; break; // arrays were declared before the built-in ran
          case 't': break;
          case 'i': case 'r': case 'x': case 'l': case 'u': case 'n': if (a[0] === '-') on += c; else off += c; break;
          default: throw new ShellError(specMessage(ctx.name, 'builtinInvalidOption', { option: '-' + c }), 2);
        }
      }
    }
    if (functions || functionNames) {
      let code = 0;
      for (const n of names.length === 0 ? [...sh.functions.keys()].sort(ordinal) : names) {
        const f = sh.functions.get(n);
        if (!f) { code = 1; continue; }
        line(ctx, functionNames ? (names.length === 0 ? `declare -f ${n}` : n) : `${n} () \n${f.source}`);
      }
      return code;
    }
    if (names.length === 0) {
      // declare / declare -p / readonly / declare -x ...: list what has those attributes
      for (const n of allVariableNames(sh)) {
        if (n === '__topwater_element') continue;
        if (![...on + listOnly].every(c => attributeFlags(sh, n).includes(c))) continue;
        const l = declareLine(sh, n);
        if (!l) continue;
        line(ctx, print || on.length + listOnly.length > 0 ? l : `${n}=${sh.arrays.has(n) ? l.slice(l.indexOf('=') + 1) : shellQuote(sh.environment.get(n)!)}`);
      }
      return 0;
    }
    let code = 0;
    if (print) {
      for (const n of names) {
        const l = declareLine(sh, n);
        if (l) line(ctx, l); else { ctx.stderr.write(`${ctx.shell.errPrefix}${ctx.name}: ${specMessage(ctx.name, 'notFound', { name: n })}\n`); code = 1; }
      }
      return code;
    }
    const local = ctx.name === 'local' || ((ctx.name === 'declare' || ctx.name === 'typeset') && sh.inFunction && !global);
    for (const arg of names) {
      const eq = arg.indexOf('=');
      const append = eq > 0 && arg[eq - 1] === '+';
      const name = eq > 0 ? arg.slice(0, append ? eq - 1 : eq) : arg;
      const value = eq > 0 ? arg.slice(eq + 1) : null;
      try {
        if (!isName(name)) throw new ShellError(specFormat(DIAGNOSTICS.notValidIdentifier, { name: arg }));
        if (local && !sh.arrays.has(name)) sh.declareLocal(name, null); // shortcut: `local x` is empty here, unset in bash
        else if (local && sh.arrays.has(name) && value !== null) sh.declareLocal(name, null);
        if (on.includes('n')) {
          // a nameref holds the name of the variable it stands for
          sh.setAttrs(name, 'n');
          sh.markShellVariable(name);
          if (value !== null) sh.environment.set(name, value);
          continue;
        }
        const target = sh.resolve(name);
        sh.setAttrs(target, on.replace('r', ''), off);
        if (value !== null) sh.setVar(name, value, append);
        else if (!sh.environment.has(target) && !sh.arrays.has(target)) { sh.markShellVariable(target); sh.environment.set(target, ''); }
        if (on.includes('r')) sh.setAttrs(target, 'r');
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        ctx.stderr.write(`${ctx.shell.errPrefix}${ctx.name}: ${e.message}\n`);
        code = 1;
      }
    }
    return code;
  };
  reg('local', declare);
  reg('declare', declare);
  reg('typeset', declare);
  reg('readonly', declare);

  reg('read', ctx => {
    const a = Args.parse(ctx.args, specOf('read'));
    const sh = ctx.shell;
    const names = a.positional.length > 0 ? a.positional : ['REPLY'];
    const input = inputFor(ctx, a.value('u'));
    const dv = a.value('d');
    const delim = dv !== undefined ? (dv.length === 0 ? '\0' : dv[0]) : '\n';
    let text: string | null;
    const exact = a.value('N'), upTo = a.value('n');
    if (exact !== undefined) text = input.readChars(Number(exact), null);
    else if (upTo !== undefined) text = input.readChars(Number(upTo), delim);
    else {
      text = input.readLine(delim);
      // without -r, a backslash at the end of a line joins it to the next one
      while (!a.has('r') && text !== null && input.lastTerminated && delim === '\n' && /(^|[^\\])(\\\\)*\\$/.test(text)) {
        const more = input.readLine();
        if (more === null) break;
        text = text.slice(0, -1) + more;
      }
    }
    if (text === null) { for (const n of names) sh.setVar(n, ''); return 1; }
    if (!a.has('r')) text = text.replace(/\\(.)/gs, '$1');
    const ifs = sh.ifs;
    if (a.has('a')) {
      // read -a NAME: every field becomes an element
      sh.unset(names[0]);
      const array = sh.writable(names[0]);
      for (const field of splitFields(text, ifs)) { sh.chargeVariable(names[0], field.length, 0); array.set(String(array.next), field); }
    } else if (a.positional.length === 0) sh.setVar('REPLY', text);
    else {
      const fields = readFields(text, ifs, names.length);
      names.forEach((n, i) => sh.setVar(n, fields[i] ?? ''));
    }
    // like bash, a final line with no trailing newline is still assigned, but read reports end of input
    return input.lastTerminated ? 0 : 1;
  });

  const mapfile: CommandHandler = ctx => {
    const a = Args.parse(ctx.args, specOf(ctx.name));
    const sh = ctx.shell;
    const name = a.positional[0] ?? 'MAPFILE';
    const input = inputFor(ctx, a.value('u'));
    const dv = a.value('d');
    const delim = dv !== undefined ? (dv.length === 0 ? '\0' : dv[0]) : '\n';
    const max = Number(a.value('n') ?? 0);
    let skip = Number(a.value('s') ?? 0);
    let index = Number(a.value('O') ?? 0);
    if (a.value('O') === undefined) sh.unset(name); // -O keeps what the array already holds
    const array = sh.writable(name);
    for (let count = 0; max === 0 || count < max;) {
      const l = input.readLine(delim);
      if (l === null) break;
      if (skip > 0) { skip--; continue; }
      const item = a.has('t') || !input.lastTerminated ? l : l + delim;
      sh.chargeVariable(name, item.length, 0);
      array.set(String(index++), item);
      count++;
    }
    return 0;
  };
  reg('mapfile', mapfile);
  reg('readarray', mapfile);

  const printEnv = (ctx: CommandContext) => {
    for (const k of [...ctx.shell.environment.keys()].sort(ordinal)) if (ctx.shell.isExported(k)) line(ctx, `${k}=${ctx.shell.environment.get(k)}`);
  };

  reg('printenv', ctx => {
    if (ctx.args.length === 0) { printEnv(ctx); return 0; }
    let code = 0;
    for (const n of ctx.args) { const v = ctx.shell.environment.get(n); if (v !== undefined && ctx.shell.isExported(n)) line(ctx, v); else code = 1; }
    return code;
  });
}
