// Shell: simple commands, invoke, script files, PATH and hash lookup, functions. See docs/architecture.md.
import type { Shell } from '../shell.js';
import { VirtualFileSystem, ShellError } from '../vfs.js';
import { StdinBuffer, LimitExceeded, DeadlineExceeded, UsageException, isShellBuiltin, usageError, type CommandContext, type RegisteredCommand } from '../command.js';
import { parseWords, literal, type Command, type Word } from '../parser.js';
import { ArithError } from '../arith.js';
import { quoteValue, shellQuote } from '../format.js';
import { COMMANDS, ERRNO, diagnostic } from '../spec.generated.js';
import { type Io, ControlFlow, ReturnSignal, ExitSignal, EXECUTABLE_STUB } from './types.js';

export class InvocationPart {
  // ---------- simple commands ----------

  /** @internal */ async execSimple(this: Shell, c: Extract<Command, { k: 'simple' }>, io: Io): Promise<number> {
    this.lineno = this.lineBase + c.line;
    this.checkTime();
    const debug = this.traps.size > 0 && !this.inDebugTrap ? this.traps.get('DEBUG') : undefined;
    if (debug) {
      // shortcut: the DEBUG trap runs before simple commands only, not before (( )), [[ ]], for and case
      this.inDebugTrap = true;
      const status = this.lastExitCode;
      try { await this.runScriptText(debug, io, false); } finally { this.inDebugTrap = false; this.lastExitCode = status; }
    }
    let words = c.words;
    const first = words.length > 0 ? literal(words[0]) : null;
    const declaration = words.length > 1 && (first === 'local' || first === 'declare' || first === 'typeset' || first === 'readonly' || first === 'export');
    if (declaration && first !== 'export') {
      const rest = await this.declareArrays(words, io);
      if (rest) {
        if (rest.length === 1) return 0;
        words = rest;
      }
    }
    let argv = declaration ? await this.expandDeclaration(words, io) : await this.expandWords(words, io);
    const assigned: [name: string, value: string, append: boolean][] = [];
    for (const a of c.assigns) {
      try {
        if (a.elements !== undefined || a.index !== undefined) { await this.assignArray(a, io); continue; }
        const v = await this.expandAssignment(a.value, io);
        if (this.xtrace) io.err.write(`+ ${a.name}${a.append ? '+=' : '='}${traceQuote(v)}\n`);
        if (words.length > 0) { assigned.push([a.name, v, a.append]); continue; }
        // with no command, assignments apply left to right: IFS=, parts=($csv) splits on the comma
        this.setVar(a.name, v, a.append);
      } catch (e) {
        if (e instanceof ArithError && a.index !== undefined) throw this.fatal(io, e.message, 1); // a[1/0]=x ends the script
        if (e instanceof ShellError && words.length === 0 && e.message.endsWith('readonly variable')) {
          io.err.write(`${this.errPrefix}${e.message}\n`);
          throw new ExitSignal(1); // like bash, assigning a read-only variable ends the script
        }
        throw e;
      }
    }
    if (argv.length === 0) {
      for (const [k, v, append] of assigned) this.setVar(k, v, append);
      // only a command substitution leaves its exit code behind; `$empty` alone is a successful no-op
      const hasSub = [...c.words, ...c.assigns.map(a => a.value)].some(w => w.parts.some(p => p.k === 'cmd'));
      return hasSub ? this.lastExitCode : 0;
    }
    const alias = this.aliases.get(argv[0]);
    if (alias !== undefined) {
      // an alias holding a pipeline or list is shell text, not just words
      if (assigned.length === 0 && /[|;&<>]/.test(alias)) {
        const name = argv[0];
        this.aliases.delete(name); // removed while it runs so it can't expand itself
        try { return await this.runScriptText(alias + ' ' + argv.slice(1).map(shellQuote).join(' '), io, false); } finally { this.aliases.set(name, alias); }
      }
      argv = [...await this.expandWords(parseWords(alias), io), ...argv.slice(1)];
      if (argv.length === 0) return 0;
    }
    if (this.xtrace) io.err.write('+ ' + argv.map(traceQuote).join(' ') + '\n');
    try {
      if (assigned.length === 0) return await this.invoke(argv, io);
      // VAR=x cmd: the variable only exists for that command
      const saved = assigned.map(([k]) => [k, this.environment.get(k), this.varAttrs.get(k)] as const);
      for (const [k, v, append] of assigned) {
        this.environment.set(k, append ? (this.environment.get(k) ?? '') + v : v);
        this.setAttrs(k, 'x'); // exported to the command
      }
      try { return await this.invoke(argv, io); } finally {
        for (const [k, v, attrs] of saved) {
          if (v === undefined) this.environment.delete(k); else this.environment.set(k, v);
          if (attrs === undefined) this.varAttrs.delete(k); else this.varAttrs.set(k, attrs);
        }
      }
    } finally { this.lastArg = argv[argv.length - 1]; }
  }

  /** @internal `local x="a b" y=$(cmd)`: arguments that look like assignments are expanded as assignments (no splitting). */
  async expandDeclaration(this: Shell, words: Word[], io: Io): Promise<string[]> {
    const argv: string[] = [];
    for (const w of words) {
      const a = w.assign;
      if (argv.length > 0 && a && a.elements === undefined && a.index === undefined)
        argv.push(a.name + (a.append ? '+=' : '=') + await this.expandAssignment(a.value, io));
      else argv.push(...await this.expandWords([w], io));
    }
    return argv;
  }

  /** @internal bash's own "command not found" line, and nothing else (the tool description already points at `help`). */
  reportNotFound(this: Shell, io: Io, name: string) {
    io.err.write(this.errPrefix + diagnostic('commandNotFound', { command: name }) + '\n');
  }

  // ---------- invoke ----------

  /** @internal Runs argv: a function, a script file, or a registered command. `skipFunctions`: `command ls` / `builtin cd`. */
  async invoke(this: Shell, argv: string[], io: Io, skipFunctions = false): Promise<number> {
    let name = argv[0];
    const fn = skipFunctions ? undefined : this.functions.get(name);
    if (fn) return this.callFunction(fn.body, argv, io);
    if (name.includes('/')) {
      const path = this.resolvePath(name);
      const dir = VirtualFileSystem.getParent(path);
      if ((dir === '/usr/bin' || dir === '/bin' || dir === '/usr/local/bin') && this.commands.has(VirtualFileSystem.getName(path)))
        name = VirtualFileSystem.getName(path);
      else return this.runScriptFile(name, path, argv, io);
    } else if (!isShellBuiltin(this, name) && !this.isUnfiled(name)) {
      let found = this.hashLookup(name);
      if (found === null) {
        found = this.searchPath(name);
        if (found === null) {
          if (!this.environment.get('PATH')) io.err.write(`${this.errPrefix}${name}: No such file or directory\n`);
          else this.reportNotFound(io, name);
          return 127;
        }
        if (found.includes('/')) this.hashed.set(name, [found, 1]); // with PATH= bash remembers nothing
      }
      const registered = this.stubCommand(found);
      if (registered === null) return this.runScriptFile(found, this.resolvePath(found), [found, ...argv.slice(1)], io);
      name = registered;
    }
    const cmd = this.commands.get(name);
    if (!cmd) {
      this.reportNotFound(io, argv[0]);
      return 127;
    }
    const ctx = this.makeContext(name, argv.slice(1), io);
    if (argv.length === 2 && this.builtinNames?.has(name) && standardOption(ctx, cmd, argv[1])) return 0;
    try {
      const r = await cmd.handler(ctx);
      return typeof r === 'number' ? r : 0;
    } catch (e) {
      if (e instanceof ControlFlow || e instanceof LimitExceeded || e instanceof DeadlineExceeded) throw e;
      if (e instanceof UsageException && !isShellBuiltin(this, name)) {
        io.err.write(usageError(name, e.message));
        return COMMANDS.get(name)?.usageExitCode ?? 1;
      }
      if (e instanceof ShellError) {
        io.err.write(`${e instanceof UsageException || isShellBuiltin(this, name) ? this.errPrefix : ''}${name}: ${e.message}\n`);
        return e instanceof UsageException ? 2 : e.exitCode;
      }
      if (e instanceof RangeError && /call stack/i.test(e.message)) throw new LimitExceeded('maximum nesting level exceeded');
      io.err.write(`${name}: ${e instanceof Error ? e.message : String(e)}\n`);
      return 1;
    }
  }

  /** @internal */
  makeContext(this: Shell, name: string, args: string[], io: Io): CommandContext {
    let stdinText: string | undefined;
    const shell = this;
    return {
      shell, name, args, stdout: io.out, stderr: io.err, fs: this.agentFs, fds: io.fds, stdin: io.stdin,
      get stdinText() { return stdinText ??= io.stdin.readAll(); },
      readLine: d => io.stdin.readLine(d),
      resolvePath: p => this.resolvePath(p),
      checkTime: () => this.checkTime(),
      remainingMs: () => this.remainingMs(),
      invoke: (argv, stdin = '') => this.invoke(argv, { stdin: new StdinBuffer(stdin), out: io.out, err: io.err }),
    };
  }

  // ---------- script files ----------

  /** @internal */ async runScriptFile(this: Shell, name: string, path: string, argv: string[], io: Io): Promise<number> {
    if (this.agentFs.isDirectory(path)) { io.err.write(`${this.errPrefix}${name}: Is a directory\n`); return 126; }
    if (!this.agentFs.isFile(path)) { io.err.write(`${this.errPrefix}${name}: ${this.agentFs.whyMissing(path)}\n`); return this.agentFs.whyMissing(path) === 'Permission denied' ? 126 : 127; }
    if (!this.agentFs.canExecute(path) || !this.agentFs.canRead(path)) { io.err.write(`${this.errPrefix}${name}: Permission denied\n`); return 126; }
    const text = this.agentFs.readText(path);
    if (text.startsWith(EXECUTABLE_STUB.slice(0, 4))) {
      const stub = this.stubCommand(path);
      if (stub !== null) return this.invoke([stub, ...argv.slice(1)], io, true);
      io.err.write(this.errPrefix + diagnostic('cannotExecuteBinary', { name: name }) + '\n');
      return 126;
    }
    if (text.startsWith('#!')) {
      const nl = text.indexOf('\n');
      const line = text.slice(2, nl >= 0 ? nl : text.length).trim();
      if (!/(^|\/|\s)(ba)?sh(\s|$)/.test(line)) {
        // the hand-off can lead back to this script (#!/usr/bin/env self), so it counts as a nesting level
        if (++this.scriptDepth > 100) throw new LimitExceeded('maximum script nesting level exceeded (100)');
        try { return await this.runInterpreter(name, line, argv, io); } finally { this.scriptDepth--; }
      }
    }
    return this.runScriptText(text, io, true, argv.slice(1), { arg0: name, file: name });
  }

  /** @internal A #! line naming another program: like the kernel, run it with its one optional argument, the script and the
   * script's arguments, when it is one of the shell's commands. */
  async runInterpreter(this: Shell, name: string, line: string, argv: string[], io: Io): Promise<number> {
    const m = /^(\S+)(?:[ \t]+(.*))?$/.exec(line)!;
    const program = this.stubCommand(m[1]);
    const interp = this.resolvePath(m[1]);
    if (program === null && this.agentFs.isFile(interp) && this.agentFs.canExecute(interp)) {
      // another script as the interpreter: it runs with this script's path as its argument. Like Linux, a chain of
      // such scripts more than a few deep (or one naming itself) is refused before anything runs.
      for (let p = interp, depth = 1; ; depth++) {
        const first = this.agentFs.canRead(p) ? this.agentFs.readText(p).split('\n', 1)[0] : '';
        const next = /^#!\s*(\S+)/.exec(first)?.[1];
        if (next === undefined || /(^|\/)(ba)?sh$/.test(next) || this.stubCommand(next) !== null) break;
        const abs = this.resolvePath(next);
        if (!this.agentFs.isFile(abs) || !this.agentFs.canExecute(abs)) break;
        if (depth >= 4) {
          io.err.write(`bash: ${diagnostic('badInterpreter', { name, interpreter: m[1], reason: ERRNO.ELOOP })}\n`);
          return 126;
        }
        p = abs;
      }
      const opt = (m[2] ?? '').trim();
      return this.invoke([m[1], ...(opt ? [opt] : []), name, ...argv.slice(1)], io, true);
    }
    if (program === null) {
      io.err.write(this.errPrefix + diagnostic('requiredFileNotFound', { name: name }) + '\n');
      return 127;
    }
    const opt = (m[2] ?? '').trim();
    const words = !opt ? [] : program === 'env' && opt.startsWith('-S') ? opt.slice(2).split(/[ \t]+/).filter(Boolean) : [opt];
    if (program !== 'env') return this.invoke([program, ...words, name, ...argv.slice(1)], io, true);
    // shortcut: env in a #! line only looks the program up on PATH; env's own options are not supported
    if (!words.length || !this.isUnfiled(words[0]) && !this.searchPathAll(words[0]).length) {
      io.err.write(`${m[1]}: \u2018${words[0] ?? ''}\u2019: No such file or directory\n`);
      if (opt.includes(' ')) io.err.write(`${m[1]}: use -[v]S to pass options in shebang lines\n`);
      return 127;
    }
    return this.invoke([...words, name, ...argv.slice(1)], io, true);
  }

  // ---------- lookup ----------

  /** @internal A registered command with no program file (a shell-only builtin, or an app command added where there is
   * no /usr/bin): it runs whatever PATH says. */
  isUnfiled(this: Shell, name: string): boolean {
    return this.commands.has(name) && !name.includes('/') && !this.fs.isFile('/usr/bin/' + name) && !this.fs.isFile('/bin/' + name);
  }

  /** @internal The registered command a program file stands for, or null when `path` is not one of the stubs. */
  stubCommand(this: Shell, path: string): string | null {
    const p = this.resolvePath(path);
    if (!this.agentFs.isFile(p) || !this.agentFs.canRead(p) || this.agentFs.readText(p) !== EXECUTABLE_STUB) return null;
    const name = VirtualFileSystem.getName(this.agentFs.realPath(p) ?? p);
    return this.commands.has(name) ? name : null;
  }

  /** @internal Where PATH would have `name`, directory by directory, as written; an empty entry is the cwd, and a leading ~ is
   * expanded as bash does during the search. */
  pathCandidates(this: Shell, name: string): string[] {
    if (this.environment.get('PATH') === '') return [name]; // PATH= runs `name` from the current directory, as typed
    return (this.environment.get('PATH') ?? '').split(':').map(d => { const tp = this.tildePrefix(d); return tp ? tp[0] + d.slice(tp[1]) : d; })
      .map(d => d ? d.replace(/\/+$/, '') + '/' + name : './' + name);
  }

  /** @internal PATH search as bash does it: the first executable regular file named `name` in a directory the agent can
   * search, else the first one that isn't executable (running it says "Permission denied"), else null. */
  searchPath(this: Shell, name: string): string | null {
    let denied: string | null = null;
    for (const p of this.pathCandidates(name)) {
      const abs = this.resolvePath(p);
      if (!this.agentFs.isFile(abs)) continue;
      if (this.agentFs.canExecute(abs)) return p;
      denied ??= p;
    }
    return denied;
  }

  /** @internal `type -a`, `which -a`: every executable regular file named `name` on PATH. */
  searchPathAll(this: Shell, name: string): string[] {
    return this.pathCandidates(name).filter(p => this.agentFs.isFile(this.resolvePath(p)) && this.agentFs.canExecute(this.resolvePath(p)));
  }

  /** @internal The hash table's entry for `name`, counting a hit; the table empties first if PATH changed. */
  hashLookup(this: Shell, name: string): string | null {
    this.syncHashed();
    const h = this.hashed.get(name);
    if (!h) return null;
    h[1]++;
    return h[0];
  }

  /** @internal */
  syncHashed(this: Shell) {
    const path = this.environment.get('PATH');
    if (path !== this.hashedFor) { this.hashed = new Map(); this.hashedFor = path; }
  }

  /** @internal `type`, `command -v`: the program `name` runs, from the hash table (counting a hit) or PATH; an app command
   * with no program file is reported in /usr/bin as before. */
  locate(this: Shell, name: string): [string, boolean] | null {
    if (name.includes('/')) {
      const abs = this.resolvePath(name);
      return this.agentFs.isFile(abs) && this.agentFs.canExecute(abs) ? [name, false] : null;
    }
    const h = this.hashLookup(name);
    if (h !== null) return [h, true];
    const p = this.searchPath(name);
    if (p !== null) return [p, false];
    return this.isAppOnly(name) ? ['/usr/bin/' + name, false] : null;
  }

  /** @internal `type -a`, `which`: every program file `name` could run, in PATH order. */
  locateAll(this: Shell, name: string): string[] {
    if (name.includes('/')) { const l = this.locate(name); return l ? [l[0]] : []; }
    const all = this.searchPathAll(name);
    return all.length || !this.isAppOnly(name) ? all : ['/usr/bin/' + name];
  }

  /** @internal */ isAppOnly(this: Shell, name: string) { return this.isUnfiled(name) && !isShellBuiltin(this, name) && !(this.builtinNames?.has(name) && COMMANDS.get(name)?.programFile === false); }

  // ---------- functions ----------

  /** @internal */ async callFunction(this: Shell, body: Command, argv: string[], io: Io): Promise<number> {
    if (this.locals.length >= 200) throw new LimitExceeded(`${argv[0]}: maximum function nesting level exceeded (200)`);
    const savedPos = this.positional;
    const onReturn = this.traps.get('RETURN');
    this.positional = argv.slice(1);
    this.locals.push(new Map());
    this.localArrays.push(new Map());
    this.funcNames.push(argv[0]);
    const where = this.funcWhere.get(argv[0]) ?? 'environment', line = this.lineno, base = this.lineBase;
    this.lineBase = 0; // a function's lines are those of the text that defined it
    this.errWhere.push({ name: where, file: where !== 'environment' });
    let code: number;
    try { code = await this.execCommand(body, io); } catch (e) {
      if (!(e instanceof ReturnSignal)) throw e;
      code = e.code;
    } finally {
      // shadowed values come back without passing chargeVariable, so they count again
      for (const [k, v] of this.localArrays.pop()!) {
        if (v === undefined) this.arrays.delete(k);
        else { this.arrays.set(k, v); for (const [a, b] of v.items) this.variableChars += a.length + b.length; }
      }
      for (const [k, { value, attrs }] of this.locals.pop()!) {
        if (value === undefined) this.environment.delete(k); else { this.environment.set(k, value); this.variableChars += value.length; }
        if (attrs === undefined) this.varAttrs.delete(k); else this.varAttrs.set(k, attrs);
      }
      this.positional = savedPos;
      this.funcNames.pop();
      this.errWhere.pop();
      this.lineno = line;
      this.lineBase = base;
    }
    // a RETURN trap set inside the function runs as it returns
    const trap = this.traps.get('RETURN');
    if (trap && trap !== onReturn) {
      this.traps.delete('RETURN');
      await this.runScriptText(trap, io, false);
    }
    return this.lastExitCode = code;
  }

  /** @internal True for a built-in command (not one the app registered or replaced). */
  isBuiltin(this: Shell, name: string): boolean { return this.builtinNames?.has(name) === true; }
}

// ---------- helpers ----------

/** set -x: an argument as bash traces it, quoted only when it needs to be. */
export function traceQuote(s: string): string { return /^[A-Za-z0-9_./=:,+@%^-]+$/.test(s) ? s : quoteValue(s); }

/** `tool --version` / `tool --help` for built-ins, so probing them looks like a real system. */
export function standardOption(ctx: CommandContext, cmd: RegisteredCommand, arg: string): boolean {
  const spec = COMMANDS.get(cmd.name);
  if ((arg !== '--version' && arg !== '--help') || !spec?.standardOptions) return false;
  if (arg === '--version') ctx.stdout.write(spec.version + '\n');
  else ctx.stdout.write(cmd.usage ?? `Usage: ${cmd.name} [OPTION]... [ARG]...\n${cmd.description}.\n`);
  return true;
}
