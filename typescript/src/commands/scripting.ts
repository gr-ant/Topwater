// Scripting built-ins: echo, printf, test, type, source, help, exit, return, trap, set, getopts, eval, command ... See docs/architecture.md.
import type { Shell } from '../shell.js';
import { diagnostic } from '../spec.generated.js';
import { Args, specOf, specMessage, ShellError, UsageException, isShellBuiltin, StdinBuffer, ioOf, line, type CommandContext, type CommandHandler } from '../command.js';
import { evalArith } from '../arith.js';
import { printfFormat, unescape, shellQuote, quoteValue } from '../format.js';
import { ordinal } from '../vfs.js';
import { ExitSignal, ReturnSignal } from '../shell/types.js';
import { allVariableNames, declareLine } from './variables.js';

/** A unary test (-f, -z, ...). */
export function testUnary(shell: Shell, op: string, arg: string): boolean {
  const path = shell.resolvePath(arg);
  const fs = shell.agentFs;
  switch (op) {
    case '-z': return arg.length === 0;
    case '-n': return arg.length > 0;
    case '-e': return fs.exists(path);
    case '-r': return fs.canRead(path);
    case '-w': return fs.canWrite(path);
    case '-x': return fs.canExecute(path);
    case '-f': return fs.isFile(path);
    case '-d': return fs.isDirectory(path);
    case '-s': return fs.isFile(path) && fs.stat(path).size > 0;
    case '-L': case '-h': return fs.isSymlink(path);
    case '-b': case '-c': case '-p': case '-S': case '-t': case '-g': case '-u': case '-k': return false; // no devices, pipes, terminals
    case '-O': case '-G': case '-N': return fs.exists(path);
    case '-v': return shell.isVariableSet(arg);
    case '-o': return shell.optionList.some(([n, on]) => n === arg && on);
  }
  throw new ShellError(specMessage('test', 'unaryExpected', { op: op }), 2);
}

/** -nt / -ot: a missing file is older than any existing one, as in bash. */
export function testNewer(shell: Shell, left: string, right: string): boolean {
  const time = (p: string) => (shell.agentFs.exists(shell.resolvePath(p)) ? shell.agentFs.stat(shell.resolvePath(p)).modified : null);
  const l = time(left), r = time(right);
  return l !== null && (r === null || l > r);
}

const BINARY_OPS = new Set(['=', '==', '!=', '<', '>', '-eq', '-ne', '-lt', '-le', '-gt', '-ge', '-nt', '-ot', '-ef']);

function testBinary(shell: Shell, left: string, op: string, right: string): boolean {
  switch (op) {
    case '=': case '==': return left === right;
    case '!=': return left !== right;
    case '<': return left < right;
    case '>': return left > right;
    case '-nt': return testNewer(shell, left, right);
    case '-ot': return testNewer(shell, right, left);
    case '-ef': return shell.agentFs.sameFile(shell.resolvePath(left), shell.resolvePath(right));
  }
  const okL = /^\s*[-+]?\d+\s*$/.test(left), okR = /^\s*[-+]?\d+\s*$/.test(right);
  if (!okL || !okR) throw new ShellError(specMessage('test', 'integerExpected', { value: okL ? right : left }), 2);
  const l = BigInt(left.trim()), r = BigInt(right.trim());
  return op === '-eq' ? l === r : op === '-ne' ? l !== r : op === '-lt' ? l < r : op === '-le' ? l <= r : op === '-gt' ? l > r : l >= r;
}

/** test / [ ]: POSIX rules for up to 3 arguments, then ! ( ) -a -o with the usual precedence. */
function testEvaluate(shell: Shell, a: string[]): boolean {
  const n = a.length;
  switch (n) {
    case 0: return false;
    case 1: return a[0].length > 0;
    case 2: return a[0] === '!' ? a[1].length === 0 : testUnary(shell, a[0], a[1]);
    case 3:
      if (BINARY_OPS.has(a[1])) return testBinary(shell, a[0], a[1], a[2]);
      if (a[1] === '-a' || a[1] === '-o') return a[1] === '-a' ? a[0].length > 0 && a[2].length > 0 : a[0].length > 0 || a[2].length > 0;
      if (a[0] === '!') return !testEvaluate(shell, a.slice(1));
      if (a[0] === '(' && a[2] === ')') return a[1].length > 0;
      throw new ShellError(specMessage('test', 'binaryExpected', { op: a[1] }), 2);
    case 4: if (a[0] === '!') return !testEvaluate(shell, a.slice(1));
  }
  let pos = 0;
  const peek = () => a[pos];
  const or = (): boolean => { let v = and(); while (peek() === '-o') { pos++; const r = and(); v = v || r; } return v; };
  const and = (): boolean => { let v = not(); while (peek() === '-a') { pos++; const r = not(); v = v && r; } return v; };
  const not = (): boolean => { if (peek() === '!') { pos++; return !not(); } return primary(); };
  const primary = (): boolean => {
    const t = peek();
    if (t === undefined) throw new ShellError(specMessage('test', 'argumentExpected'), 2);
    if (t === '(') {
      pos++;
      const v = or();
      if (peek() !== ')') throw new ShellError(specMessage('test', 'parenExpected'), 2);
      pos++;
      return v;
    }
    if (pos + 2 < n && BINARY_OPS.has(a[pos + 1])) { pos += 3; return testBinary(shell, t, a[pos - 2], a[pos - 1]); }
    if (t.length === 2 && t[0] === '-' && pos + 1 < n) { pos += 2; return testUnary(shell, t, a[pos - 1]); }
    pos++;
    return t.length > 0;
  };
  const result = or();
  if (pos < n) throw new ShellError(specMessage('test', 'tooManyArguments'), 2);
  return result;
}

const SHELL_KEYWORDS = new Set(['if', 'then', 'else', 'elif', 'fi', 'case', 'esac', 'for', 'select', 'while', 'until', 'do', 'done', 'in', 'function', 'time', '{', '}', '!', '[[', ']]', 'coproc']);

function parseCount(ctx: CommandContext, fallback: number): number {
  if (ctx.args.length === 0) return fallback;
  if (!/^-?\d+$/.test(ctx.args[0])) throw new ShellError(specMessage(ctx.name, 'numericArgument', { value: ctx.args[0] }), 2);
  return Number(ctx.args[0]);
}

function loopJump(ctx: CommandContext, isBreak: boolean): number {
  if (ctx.shell.loopDepth > 0) ctx.shell.loopControl(isBreak, Math.max(1, parseCount(ctx, 1)));
  ctx.stderr.write(`${ctx.shell.errPrefix}${ctx.name}: ${specMessage(ctx.name, 'onlyInLoop')}\n`);
  return 0;
}

/** `type NAME` / `command -V NAME`: what the name runs, or false (with a message) when nothing. */
function describe(ctx: CommandContext, a: string, all = false): boolean {
  const sh = ctx.shell;
  const alias = sh.aliases.get(a);
  const fn = sh.functions.get(a);
  const lines: string[] = [];
  if (alias !== undefined) lines.push(`${a} is aliased to \`${alias}'`);
  if (SHELL_KEYWORDS.has(a)) lines.push(`${a} is a shell keyword`);
  if (fn) lines.push(`${a} is a function\n${a} () \n${fn.source}`);
  if (isShellBuiltin(sh, a)) lines.push(`${a} is a shell builtin`);
  if (all) lines.push(...sh.locateAll(a).map(p => `${a} is ${p}`));
  else if (!lines.length) {
    const l = sh.locate(a);
    if (l) lines.push(l[1] ? `${a} is hashed (${l[0].startsWith('/') || l[0].startsWith('./') ? '' : './'}${l[0]})` : `${a} is ${l[0].includes('/') ? l[0] : sh.resolvePath(l[0])}`);
  }
  if (!lines.length) { ctx.stderr.write(`${ctx.shell.errPrefix}type: ${specMessage('type', 'notFound', { name: a })}\n`); return false; }
  for (const l of all ? lines : lines.slice(0, 1)) line(ctx, l);
  return true;
}

export function registerScripting(shell: Shell) {
  const reg = (name: string, handler: CommandHandler) => shell.register(name, handler);

  reg('true', () => 0);
  reg(':', () => 0);
  reg('false', () => 1);

  reg('echo', ctx => {
    const args = [...ctx.args];
    let newline = true, escapes = false;
    while (args.length > 0 && /^-[neE]+$/.test(args[0])) {
      if (args[0].includes('n')) newline = false;
      if (args[0].includes('e')) escapes = true;
      if (args[0].includes('E')) escapes = false;
      args.shift();
    }
    let text = args.join(' ');
    if (escapes) {
      const stop = text.indexOf('\\c'); // \c: print nothing more
      if (stop >= 0) { text = text.slice(0, stop); newline = false; }
      text = unescape(text, true, true);
    }
    ctx.stdout.write(newline ? text + '\n' : text);
  });

  reg('printf', ctx => {
    const args = [...ctx.args];
    let variable: string | undefined;
    if (args.length >= 2 && args[0] === '-v') { variable = args[1]; args.splice(0, 2); }
    if (args[0] === '--') args.shift();
    if (args.length === 0) throw new ShellError(specMessage('printf', 'usage'), 2);
    const errors: string[] = [];
    const text = printfFormat(args[0], args.slice(1), errors, () => ctx.shell.now());
    for (const e of errors) ctx.stderr.write(ctx.shell.errPrefix + e + '\n'); // bash reports them as it goes, then writes the text
    if (variable === undefined) ctx.stdout.write(text); else ctx.shell.setVar(variable, text);
    return errors.length > 0 ? 1 : 0;
  });

  reg('history', ctx => {
    const h = ctx.shell.history;
    const n = ctx.args.length && /^\d+$/.test(ctx.args[0]) ? Number(ctx.args[0]) : h.length;
    for (let i = Math.max(0, h.length - n); i < h.length; i++) line(ctx, `${String(i + 1).padStart(5)}  ${h[i]}`);
  });

  reg('which', ctx => {
    let code = 0;
    for (const a of ctx.args.filter(x => !x.startsWith('-'))) {
      const found = ctx.shell.locateAll(a);
      if (!found.length) code = 1;
      for (const p of ctx.args.includes('-a') ? found : found.slice(0, 1)) line(ctx, p);
    }
    return code;
  });

  reg('type', ctx => {
    const sh = ctx.shell;
    let code = 0;
    const opts = Args.parse(ctx.args, specOf('type'));
    for (const a of opts.positional) {
      if (opts.has('t') || opts.has('p') || opts.has('P')) {
        let kind = sh.aliases.has(a) ? 'alias' : SHELL_KEYWORDS.has(a) ? 'keyword' : sh.functions.has(a) ? 'function'
          : isShellBuiltin(sh, a) ? 'builtin' : null;
        let paths: string[] = [];
        if (kind === null || opts.has('P')) { const l = opts.has('a') ? null : sh.locate(a); paths = opts.has('a') ? sh.locateAll(a) : l ? [l[0]] : []; }
        kind ??= paths.length ? 'file' : null;
        if (kind === null) { code = 1; continue; }
        if (opts.has('t')) line(ctx, kind);
        else for (const p of paths) line(ctx, p);
        continue;
      }
      if (!describe(ctx, a, opts.has('a'))) code = 1;
    }
    return code;
  });

  reg('test', ctx => (testEvaluate(ctx.shell, [...ctx.args]) ? 0 : 1));
  reg('[', ctx => {
    if (ctx.args.length === 0 || ctx.args[ctx.args.length - 1] !== ']') throw new ShellError(specMessage('[', 'missingBracket'), 2);
    return testEvaluate(ctx.shell, ctx.args.slice(0, -1)) ? 0 : 1;
  });

  const runScript: CommandHandler = async ctx => {
    const io = ioOf(ctx);
    let args = [...ctx.args];
    const sh = ctx.shell;
    const isBash = ctx.name === 'bash' || ctx.name === 'sh';
    let command = false, login = false;
    // bash's own options: -c, and -e -u -x -o pipefail ... which the new shell starts with
    const options: [string, boolean][] = [];
    let i = 0;
    for (; isBash && i < args.length && args[i].length > 1 && (args[i][0] === '-' || args[i][0] === '+'); i++) {
      const f = args[i];
      if (f === '--') { i++; break; }
      if (f.startsWith('--')) { login ||= f === '--login'; continue; } // --norc, --noprofile
      for (const c of f.slice(1)) {
        if (c === 'c') command = true;
        else if (c === 'l' && f[0] === '-') login = true;
        else if (c === 'o' && i + 1 < args.length) options.push([args[++i], f[0] === '-']);
        else if ('eufxC'.includes(c)) options.push([({ e: 'errexit', u: 'nounset', f: 'noglob', x: 'xtrace', C: 'noclobber' } as Record<string, string>)[c], f[0] === '-']);
      }
    }
    args = args.slice(i);
    const setup = () => { for (const [name, on] of options) sh.setOption(name, on); };
    if (command) {
      if (args.length === 0) throw new ShellError(specMessage(ctx.name, 'cOptionArgument'), 2);
      return sh.runScriptText(args[0], io, true, args.slice(2), { arg0: args[1] ?? ctx.name, setup, login });
    }
    if (args.length === 0) {
      if (!isBash) throw new ShellError(specMessage(ctx.name, 'filenameRequired'), 2);
      return sh.runScriptText(ctx.stdinText, { ...io, stdin: new StdinBuffer('') }, true, [], { arg0: ctx.name, setup, login });
    }
    let text: string;
    try { text = ctx.fs.readText(ctx.resolvePath(args[0])); } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      // a new bash reports its missing script itself, so without a line; source reports it at its own line
      ctx.stderr.write(`${isBash ? 'bash: ' : ctx.shell.errPrefix}${args[0]}: ${e.message.slice(e.message.lastIndexOf(': ') + 2)}\n`);
      return isBash ? 127 : 1;
    }
    if (isBash) return sh.runScriptText(text, io, true, args.slice(1), { arg0: args[0], file: args[0], setup, login });
    sh.sourceDepth++;
    try { return await sh.runScriptText(text, io, false, args.length > 1 ? args.slice(1) : undefined, undefined, args[0]); } catch (e) {
      if (e instanceof ReturnSignal) return e.code; // `return` ends the sourced file only
      throw e;
    } finally { sh.sourceDepth--; }
  };
  reg('bash', runScript);
  reg('sh', runScript);
  reg('source', runScript);
  reg('.', runScript);

  reg('help', ctx => {
    const sh = ctx.shell;
    if (ctx.args.length > 0) {
      const c = sh.commands.get(ctx.args[0]);
      if (!c) throw new ShellError(specMessage('help', 'noTopics', { topic: ctx.args[0] }));
      line(ctx, c.usage ?? `${c.name}: ${c.description}`);
      return 0;
    }
    const cmds = [...sh.commands.values()].sort((a, b) => ordinal(a.name, b.name));
    const width = Math.max(...cmds.map(c => c.name.length)) + 2;
    line(ctx, "Available commands (run '<command> --help' for details on non-standard ones):");
    for (const c of cmds) line(ctx, `  ${c.name.padEnd(width)}${c.description}`);
  });

  reg('exit', ctx => {
    if (ctx.args.length > 0 && !/^-?\d+$/.test(ctx.args[0])) {
      // bash still exits, with status 2
      ctx.stderr.write(`${ctx.shell.errPrefix}exit: ${specMessage('exit', 'numericArgument', { value: ctx.args[0] })}\n`);
      throw new ExitSignal(2);
    }
    throw new ExitSignal(parseCount(ctx, ctx.shell.lastExitCode) & 255);
  });
  reg('return', ctx => {
    if (!ctx.shell.inFunction && ctx.shell.sourceDepth === 0) throw new ShellError(specMessage('return', 'notInFunction'), 2);
    ctx.shell.returnSignal(parseCount(ctx, ctx.shell.lastExitCode) & 255);
  });
  reg('break', ctx => loopJump(ctx, true));
  reg('continue', ctx => loopJump(ctx, false));

  reg('shift', ctx => {
    const n = parseCount(ctx, 1);
    if (n > ctx.shell.positional.length) return 1;
    ctx.shell.positional = ctx.shell.positional.slice(n);
    return 0;
  });

  reg('trap', ctx => {
    // EXIT runs when the tool call or script ends, ERR after a failing command, RETURN as a function returns;
    // other signals never arrive here
    const args = ctx.args.filter(a => a !== '--');
    const traps = ctx.shell.traps;
    const sigName = (sig: string) => {
      let name = sig.toUpperCase();
      if (name.startsWith('SIG')) name = name.slice(3);
      return name === '0' ? 'EXIT' : name;
    };
    if (args.length === 0 || args[0] === '-p') {
      const only = new Set(args.slice(1).map(sigName));
      for (const [sig, action] of traps) if (only.size === 0 || only.has(sig)) line(ctx, `trap -- ${quoteValue(action)} ${sig}`);
      return 0;
    }
    if (args[0] === '-l') { line(ctx, ' 1) SIGHUP\t 2) SIGINT\t 3) SIGQUIT\t 9) SIGKILL\t15) SIGTERM'); return 0; }
    const [act, signals] = args.length === 1 ? [null, args] : [args[0], args.slice(1)];
    for (const sig of signals) if (act === null || act === '-') traps.delete(sigName(sig)); else traps.set(sigName(sig), act);
    return 0;
  });

  const KNOWN_SHOPTS = ['assoc_expand_once', 'autocd', 'cdable_vars', 'cdspell', 'checkhash', 'checkjobs', 'checkwinsize', 'cmdhist',
    'complete_fullquote', 'direxpand', 'dirspell', 'dotglob', 'execfail', 'expand_aliases', 'extdebug', 'extglob', 'extquote',
    'failglob', 'force_fignore', 'globasciiranges', 'globskipdots', 'globstar', 'gnu_errfmt', 'histappend', 'histreedit',
    'histverify', 'hostcomplete', 'huponexit', 'inherit_errexit', 'interactive_comments', 'lastpipe', 'lithist',
    'localvar_inherit', 'localvar_unset', 'login_shell', 'mailwarn', 'no_empty_cmd_completion', 'nocaseglob', 'nocasematch',
    'noexpand_translation', 'nullglob', 'patsub_replacement', 'progcomp', 'progcomp_alias', 'promptvars', 'restricted_shell',
    'shift_verbose', 'sourcepath', 'varredir_close', 'xpg_echo'];
  reg('shopt', ctx => {
    const sh = ctx.shell;
    const a = Args.parse(ctx.args, specOf('shopt'));
    const setting = a.has('s') || a.has('u');
    if (a.has('o')) {
      // shopt -o: the set -o options
      for (const n of a.positional) if (!sh.setOption(n, a.has('s')) && setting) throw new ShellError(specMessage('shopt', 'invalidOptionName', { name: n }));
      if (setting) return 0;
      for (const [n, on] of sh.optionList) if (a.positional.length === 0 || a.positional.includes(n)) line(ctx, `${n.padEnd(15)}\t${on ? 'on' : 'off'}`);
      return 0;
    }
    const names = a.positional.length > 0 ? a.positional : KNOWN_SHOPTS;
    if (setting) {
      for (const n of names) {
        if (!KNOWN_SHOPTS.includes(n)) throw new ShellError(specMessage('shopt', 'invalidShellOptionName', { name: n }));
        if (a.has('s')) sh.shopts.add(n); else sh.shopts.delete(n);
      }
      return 0;
    }
    let code = 0;
    for (const n of names) {
      if (!KNOWN_SHOPTS.includes(n)) { ctx.stderr.write(`${ctx.shell.errPrefix}shopt: ${specMessage('shopt', 'invalidShellOptionName', { name: n })}\n`); code = 1; continue; }
      const on = sh.shopts.has(n);
      if (code === 0 && !on) code = 1;
      if (!a.has('q')) line(ctx, `${n.padEnd(15)}\t${on ? 'on' : 'off'}`);
    }
    return code;
  });

  reg('set', ctx => {
    const args = [...ctx.args];
    const sh = ctx.shell;
    if (args.length === 0) {
      for (const n of allVariableNames(sh)) {
        const a = sh.arrays.get(n);
        if (a) { const l = declareLine(sh, n); if (l) line(ctx, `${n}=${l.slice(l.indexOf('=') + 1)}`); }
        else line(ctx, `${n}=${shellQuote(sh.environment.get(n)!)}`);
      }
      return 0;
    }
    let i = 0;
    for (; i < args.length; i++) {
      const a = args[i];
      if (a === '--') { sh.positional = args.slice(i + 1); return 0; }
      if (a === '-') { i++; break; }
      if (a.length < 2 || (a[0] !== '-' && a[0] !== '+')) break;
      const on = a[0] === '-';
      for (const c of a.slice(1)) {
        if (c === 'o') {
          if (i + 1 >= args.length) {
            // set -o / set +o alone: list the options (as commands for +o)
            for (const [n, value] of sh.optionList) line(ctx, on ? `${n.padEnd(15)}\t${value ? 'on' : 'off'}` : `set ${value ? '-' : '+'}o ${n}`);
            continue;
          }
          const opt = args[++i];
          if (!sh.setOption(opt, on)) throw new ShellError(specMessage('set', 'invalidOptionName', { name: opt }), 2);
        } else if ('eufxC'.includes(c)) sh.setOption(({ e: 'errexit', u: 'nounset', f: 'noglob', x: 'xtrace', C: 'noclobber' } as Record<string, string>)[c], on);
        else if (!'abhkmnpvBEHPT'.includes(c)) throw new ShellError(specMessage('set', 'builtinInvalidOption', { option: a[0] + c }), 2); // accepted, no effect here
      }
    }
    if (i < args.length) sh.positional = args.slice(i);
    return 0;
  });

  reg('getopts', ctx => {
    if (ctx.args.length < 2) throw new ShellError(specMessage('getopts', 'usage'), 2);
    const sh = ctx.shell;
    const [spec, name] = ctx.args;
    const args = ctx.args.length > 2 ? ctx.args.slice(2) : sh.positional;
    const silent = spec.startsWith(':');
    const oi = Number(sh.getVariable('OPTIND'));
    let optind = Number.isInteger(oi) && oi > 0 ? oi : 1;
    // where we are inside a group of options like -abc; restarts when the script resets OPTIND
    let pos = sh.getoptsState.optind === optind ? sh.getoptsState.pos : 1;
    const done = (code: number, result: string) => {
      sh.setVar(name, result);
      sh.getoptsState = { optind, pos };
      sh.setVar('OPTIND', String(optind));
      return code;
    };
    if (optind > args.length) return done(1, '?');
    const arg = args[optind - 1];
    if (pos === 1) {
      if (arg === '--') { optind++; return done(1, '?'); }
      if (arg.length < 2 || arg[0] !== '-') return done(1, '?');
    }
    const c = arg[pos];
    const at = c === ':' ? -1 : spec.indexOf(c);
    if (++pos >= arg.length) { optind++; pos = 1; }
    if (at < 0) {
      if (silent) sh.setVar('OPTARG', c);
      else { ctx.stderr.write(`${specMessage('getopts', 'illegalOption', { script: sh.getVariable('0'), option: c })}\n`); sh.unset('OPTARG'); }
      return done(0, '?');
    }
    if (spec[at + 1] !== ':') { sh.unset('OPTARG'); return done(0, c); }
    if (pos > 1) { sh.setVar('OPTARG', arg.slice(pos)); optind++; pos = 1; }
    else if (optind <= args.length) sh.setVar('OPTARG', args[optind++ - 1]);
    else if (silent) { sh.setVar('OPTARG', c); return done(0, ':'); }
    else {
      ctx.stderr.write(`${specMessage('getopts', 'missingArgument', { script: sh.getVariable('0'), option: c })}\n`);
      sh.unset('OPTARG');
      return done(0, '?');
    }
    return done(0, c);
  });

  reg('exec', async ctx => {
    // exec with only redirections is handled by the shell; with a command, the command's status ends the shell
    const args = [...ctx.args];
    while (args[0] === '-c' || args[0] === '-l') args.shift();
    if (args.length === 0) return 0;
    throw new ExitSignal(await ctx.shell.invoke(args, ioOf(ctx), true));
  });

  reg('hash', ctx => {
    const sh = ctx.shell;
    sh.syncHashed();
    const args = ctx.args;
    let code = 0, i = 0, show = false, del = false, list = false, path: string | null = null;
    for (; i < args.length && args[i].length > 1 && args[i][0] === '-'; i++) {
      if (args[i] === '--') { i++; break; }
      for (const ch of args[i].slice(1)) {
        if (ch === 'r') sh.hashed.clear();
        else if (ch === 't') show = true;
        else if (ch === 'd') del = true;
        else if (ch === 'p') { if (i + 1 >= args.length) throw new UsageException('-p: option requires an argument'); path = args[++i]; break; }
        else if (ch === 'l') list = true;
        else throw new UsageException(specMessage('hash', 'builtinInvalidOption', { option: '-' + ch }));
      }
    }
    const names = args.slice(i);
    if ((show || del) && !names.length) {
      ctx.stderr.write(`${sh.errPrefix}hash: -${show ? 't' : 'd'}: option requires an argument\n`);
      return 1;
    }
    // -l: each entry as the command that would hash it again
    const reusable = (n: string, p: string) => `builtin hash -p ${p} ${n}`;
    if (list && !names.length) ctx.stdout.write([...sh.hashed].map(([n, [p]]) => reusable(n, p) + '\n').join(''));
    else if (i === 0 && !names.length) {
      if (!sh.hashed.size) line(ctx, 'hash: hash table empty');
      else ctx.stdout.write('hits\tcommand\n' + [...sh.hashed.values()].map(([p, n]) => `${String(n).padStart(4)}\t${p}\n`).join(''));
    }
    const notFound = (n: string) => { ctx.stderr.write(`${sh.errPrefix}hash: ${specMessage('hash', 'notFound', { name: n })}\n`); code = 1; };
    for (const n of names) {
      const hashed = show ? sh.hashLookup(n) : null;
      if (hashed !== null) line(ctx, list ? reusable(n, hashed) : names.length > 1 ? `${n}\t${hashed}` : hashed);
      else if (del && sh.hashed.delete(n)) { /* removed */ }
      else if (path !== null) sh.hashed.set(n, [path, 0]);
      else if (show || del) notFound(n);
      else if (n.includes('/') || isShellBuiltin(sh, n) || sh.functions.has(n) || sh.isUnfiled(n)) { /* nothing to remember */ }
      else { const found = sh.searchPathAll(n)[0]; if (found) sh.hashed.set(n, [found, 0]); else notFound(n); }
    }
    return code;
  });

  reg('builtin', ctx => {
    if (ctx.args.length === 0) return 0;
    if (!isShellBuiltin(ctx.shell, ctx.args[0])) throw new ShellError(specMessage('builtin', 'notBuiltin', { name: ctx.args[0] }));
    return ctx.shell.invoke([...ctx.args], ioOf(ctx), true);
  });

  reg('eval', ctx => ctx.shell.runScriptText(ctx.args.join(' '), ioOf(ctx), false, undefined, undefined, undefined, 'eval'));

  reg('let', ctx => {
    if (ctx.args.length === 0) throw new ShellError(specMessage('let', 'expressionExpected'));
    let v = 0n;
    for (const expr of ctx.args) v = evalArith(ctx.shell, expr);
    return v !== 0n ? 0 : 1;
  });

  reg('command', async ctx => {
    const sh = ctx.shell;
    const args = [...ctx.args];
    let standardPath = false;
    while (args[0] === '-p') { args.shift(); standardPath = true; }
    const isDescribe = args[0] === '-v' || args[0] === '-V';
    if (!isDescribe && args.length > 0 && standardPath && !args[0].includes('/') && !isShellBuiltin(sh, args[0])) {
      // -p: search the standard directories, not $PATH
      const dir = ['/usr/bin/', '/bin/'].find(d => ctx.fs.isFile(d + args[0]) && ctx.fs.canExecute(d + args[0]));
      if (dir === undefined) { ctx.stderr.write(diagnostic('commandNotFound', { command: args[0] }) + '\n'); return 127; }
      args[0] = dir + args[0];
    }
    if (!isDescribe) return args.length === 0 ? 0 : sh.invoke(args, ioOf(ctx), true);
    let code = 0;
    for (const n of args.slice(1)) {
      if (args[0] === '-V') { if (!describe(ctx, n)) code = 1; continue; }
      const al = sh.aliases.get(n);
      if (al !== undefined) line(ctx, `alias ${n}='${al}'`);
      else if (SHELL_KEYWORDS.has(n) || sh.functions.has(n) || isShellBuiltin(sh, n)) line(ctx, n);
      else { const l = sh.locate(n); if (l) line(ctx, l[0]); else code = 1; }
    }
    return code;
  });
}
