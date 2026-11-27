// Shell: scripts, lists, jobs, pipelines, compound commands and [[ ]]. See docs/architecture.md.
import type { Shell } from '../shell.js';
import { ShellError } from '../vfs.js';
import { StdinBuffer, LimitExceeded } from '../command.js';
import { parse, literal, ParseError, type Script, type AndOr, type Pipeline, type Command, type Word } from '../parser.js';
import { evalArith } from '../arith.js';
import { globRegex } from '../glob.js';
import { posixToJs } from '../regex.js';
import { DIAGNOSTICS, diagnostic } from '../spec.generated.js';
import { testUnary, testNewer } from '../commands/builtins.js';
import { type Io, ShellArray, ControlFlow, LineAbort, LoopControl, ReturnSignal, ExitSignal, type FunctionDef, type NewProcess } from './types.js';
import { BufferWriter, FileSink, SwitchWriter } from './redirection.js';

export interface SavedState {
  cwd: string; env: Map<string, string>; aliases: Map<string, string>; functions: Map<string, FunctionDef>; positional: string[];
  errexit: boolean; pipefail: boolean; nounset: boolean; noglob: boolean; xtrace: boolean; noclobber: boolean; ifsUnset: boolean;
  shopts: Set<string>; traps: Map<string, string>; arrays: Map<string, ShellArray>; attrs: Map<string, string>; variableChars: number;
  hashed: Map<string, [string, number]>; umask: number;
}

export class ExecutionPart {
  // ---------- scripts ----------

  /** @internal Runs shell text: `eval`, `source`, traps, `bash -c`, scripts. */
  async runScriptText(this: Shell, text: string, io: Io, subshell: boolean, positional?: string[], process?: NewProcess, sourced?: string, inline?: 'eval' | 'sub'): Promise<number> {
    let script: Script;
    const base = inline ? Math.max(this.lineno, 1) - 1 : 0;
    try { script = parse(text); } catch (e) {
      if (e instanceof ParseError && inline === 'eval') {
        const at = `${this.errWhere.at(-1)?.name ?? 'bash'}: eval: line ${base + (e.line ?? 1)}: `;
        io.err.write(at + e.message + '\n');
        if (e.message.startsWith(DIAGNOSTICS.unexpectedToken.slice(0, DIAGNOSTICS.unexpectedToken.indexOf('`'))))
          io.err.write(at + diagnostic('sourceLine', { line: text.split('\n')[(e.line ?? 1) - 1] ?? '' }) + '\n');
        return 2;
      }
      if (e instanceof ParseError) { io.err.write(`bash: ${e.message}\n`); return 2; }
      throw e;
    }
    if (++this.scriptDepth > 100) throw new LimitExceeded('maximum script nesting level exceeded (100)');
    const savedArg0 = this.arg0;
    const sources = this.sources.length, where = this.errWhere.length, line = this.lineno, savedBase = this.lineBase;
    this.lineBase = base;
    const file = process?.file ?? sourced;
    if (file !== undefined) this.sources.push(file);
    if (process) this.errWhere.push({ name: process.file ?? process.arg0, file: process.file !== undefined });
    else if (sourced !== undefined) this.errWhere.push({ name: sourced, file: true });
    try {
      if (!subshell) {
        const savedPos = this.positional;
        if (positional) this.positional = positional;
        try { return await this.execScript(script, io); } finally { if (positional) this.positional = savedPos; }
      }
      const saved = this.saveState(), depth = this.subshell;
      this.traps = new Map();
      io = { ...io, out: new SwitchWriter(io.out), err: new SwitchWriter(io.err) }; // `exec >file` in a script ends with it
      // a new process ends with its own status on a fatal error, not a subshell's 1
      if (process) { this.becomeNewProcess(process); this.subshell = 0; }
      if (positional) this.positional = positional;
      try {
        if (process?.login) await this.sourceLoginFiles(io);
        try { return await this.execTopLevel(script, io); } finally { await this.runExitTrap(io); }
      } catch (e) {
        if (e instanceof ExitSignal) return e.code;
        throw e;
      } finally { this.subshell = depth; this.restoreState(saved); }
    } finally {
      this.scriptDepth--;
      this.arg0 = savedArg0;
      this.sources.length = sources;
      this.errWhere.length = where;
      this.lineno = line;
      this.lineBase = savedBase;
    }
  }

  /** @internal */ async sourceLoginFiles(this: Shell, io: Io) {
    const home = this.environment.get('HOME') ?? '', fs = this.agentFs;
    const personal = ['/.bash_profile', '/.bash_login', '/.profile'].map(f => home + f).find(f => fs.isFile(f) && fs.canRead(f));
    for (const file of ['/etc/profile', personal]) {
      if (file === undefined || !fs.isFile(file) || !fs.canRead(file)) continue;
      this.sourceDepth++;
      try { await this.runScriptText(fs.readText(file), io, false, undefined, undefined, file); } catch (e) {
        if (!(e instanceof ReturnSignal)) throw e;
      } finally { this.sourceDepth--; }
    }
  }

  /** @internal What a child bash inherits: exported variables (not their other attributes) and exported functions. */
  becomeNewProcess(this: Shell, process: NewProcess) {
    this.environment = new Map([...this.environment].filter(([k]) => this.isExported(k)));
    this.varAttrs = new Map([...this.environment.keys()].map(k => [k, 'x']));
    this.arrays = new Map();
    this.aliases = new Map();
    this.functions = new Map([...this.functions].filter(([k]) => this.exportedFunctions.has(k)));
    this.errexit = this.pipefail = this.nounset = this.xtrace = this.noglob = this.noclobber = false;
    this.hashed = new Map();
    this.arg0 = process.arg0;
    if (process.login) this.shopts.add('login_shell'); else this.shopts.delete('login_shell');
    process.setup?.();
  }

  /** @internal */ async runExitTrap(this: Shell, io: Io) {
    const action = this.traps.get('EXIT');
    this.traps.delete('EXIT');
    if (!action) return;
    const saved = this.lastExitCode;
    try { await this.runScriptText(action, io, false); } catch (e) { if (!(e instanceof ControlFlow)) throw e; }
    this.lastExitCode = saved;
  }

  // arrays are shared with the saved state and copied on first write (see writable)
  /** @internal */ saveState(this: Shell): SavedState {
    this.epoch++;
    this.subshell++;
    return {
      cwd: this.cwdPath, env: new Map(this.environment), aliases: new Map(this.aliases), functions: new Map(this.functions),
      positional: this.positional, errexit: this.errexit, pipefail: this.pipefail, nounset: this.nounset, noglob: this.noglob,
      xtrace: this.xtrace, noclobber: this.noclobber, ifsUnset: this.ifsUnset, shopts: new Set(this.shopts), traps: new Map(this.traps),
      arrays: new Map(this.arrays), attrs: new Map(this.varAttrs), variableChars: this.variableChars,
      hashed: new Map([...this.hashed].map(([k, [p, n]]) => [k, [p, n]])), umask: this.agentFs.umask,
    };
  }

  /** @internal */ restoreState(this: Shell, s: SavedState) {
    this.epoch++;
    this.subshell--;
    ({ errexit: this.errexit, pipefail: this.pipefail, nounset: this.nounset, noglob: this.noglob, xtrace: this.xtrace,
      noclobber: this.noclobber, ifsUnset: this.ifsUnset, variableChars: this.variableChars, positional: this.positional } = s);
    this.varAttrs = s.attrs;
    this.arrays = s.arrays;
    this.environment = s.env;
    this.aliases = s.aliases;
    this.functions = s.functions;
    this.shopts = s.shopts;
    this.traps = s.traps;
    this.hashed = s.hashed;
    this.agentFs.umask = s.umask;
    this.cwdPath = this.agentFs.isDirectory(s.cwd) ? s.cwd : '/';
  }

  // ---------- lists ----------

  /** @internal A shell's own script, list by list: a bad substitution skips the rest of its line and the next line runs. */
  async execTopLevel(this: Shell, script: Script, io: Io): Promise<number> {
    let code = 0;
    for (let i = 0; i < script.items.length; i++) {
      try { code = await this.execScript({ items: [script.items[i]] }, io); } catch (e) {
        if (!(e instanceof LineAbort)) throw e;
        code = this.lastExitCode = 1;
        const end = script.items[i].endLine ?? Infinity;
        while (i + 1 < script.items.length && (script.items[i + 1].line ?? 0) <= end) i++;
      }
    }
    return code;
  }

  /** @internal */ async execScript(this: Shell, script: Script, io: Io): Promise<number> {
    let code = 0;
    for (const item of script.items) {
      if (item.background) { code = await this.execBackground(item, io); continue; }
      // every command but the last of an && / || list is a condition: set -e and the ERR trap ignore it
      code = await this.execListPipeline(item.first, item.rest.length > 0, io);
      let lastRun = 0;
      let negated = item.first.negate;
      for (let k = 0; k < item.rest.length; k++) {
        const [op, next] = item.rest[k];
        if ((op === '&&') !== (code === 0)) continue;
        code = await this.execListPipeline(next, k < item.rest.length - 1, io);
        lastRun = k + 1;
        negated = next.negate;
      }
      if (code !== 0 && this.condDepth === 0 && lastRun === item.rest.length && !negated) {
        const onError = this.traps.get('ERR');
        if (onError && !this.inErrTrap) {
          this.inErrTrap = true;
          try { await this.runScriptText(onError, io, false); } finally { this.inErrTrap = false; this.lastExitCode = code; }
        }
        if (this.errexit) throw new ExitSignal(code); // set -e: never inside if/while conditions
      }
    }
    return code;
  }

  /** @internal */ async execListPipeline(this: Shell, p: Pipeline, condition: boolean, io: Io): Promise<number> {
    if (!condition) return this.execPipeline(p, io);
    this.condDepth++;
    try { return await this.execPipeline(p, io); } finally { this.condDepth--; }
  }

  // ---------- jobs ----------

  /** @internal
   * cmd &: runs now, to completion, in a subshell; $! gets a job id that `wait` reports its status for.
   * shortcut: jobs run in order, not concurrently; run them truly in parallel if agents start relying on overlap.
   */
  async execBackground(this: Shell, item: AndOr, io: Io): Promise<number> {
    const saved = this.saveState();
    let code: number;
    try { code = await this.execScript({ items: [{ ...item, background: false }] }, { ...io, stdin: new StdinBuffer('') }); } catch (e) {
      if (!(e instanceof ExitSignal)) throw e;
      code = e.code;
    } finally { this.restoreState(saved); }
    this.lastBg = ++this.nextPid;
    this.jobs.set(this.lastBg, code);
    this.lastExitCode = 0;
    return 0;
  }

  /** @internal `wait [id]`: the job's exit status (it has already finished), 127 for an unknown id. */
  waitFor(this: Shell, pid: number): number { const c = this.jobs.get(pid); this.jobs.delete(pid); return c ?? 127; }
  /** @internal `wait -n`: the status of the oldest finished job, or 127 when there are none. */
  waitNext(this: Shell): number { return this.jobs.size === 0 ? 127 : this.waitFor(Math.min(...this.jobs.keys())); }
  /** @internal */ isJob(this: Shell, pid: number) { return this.jobs.has(pid); }
  /** @internal */ forgetJobs(this: Shell) { this.jobs.clear(); }
  /** @internal */ jobList(this: Shell): number[] { return [...this.jobs.keys()]; }

  // ---------- pipelines ----------

  /** @internal */ async execPipeline(this: Shell, p: Pipeline, io: Io): Promise<number> {
    if (p.timed) {
      const start = this.now();
      const status = p.commands.length === 0 ? 0 : await this.execPipeline({ ...p, timed: false }, io);
      const t = (this.now() - start) / 1000;
      io.err.write(p.posixTime ? `real ${t.toFixed(2)}\nuser 0.00\nsys 0.00\n`
        : `\nreal\t${Math.floor(t / 60)}m${(t % 60).toFixed(3)}s\nuser\t0m0.000s\nsys\t0m0.000s\n`);
      return this.lastExitCode = status;
    }
    if (!p.negate) return this.runPipeline(p, io);
    this.condDepth++; // like a condition, a negated pipeline is exempt from set -e
    let code: number;
    try { code = await this.runPipeline(p, io); } finally { this.condDepth--; }
    return this.lastExitCode = code === 0 ? 1 : 0;
  }

  /** @internal */ async runPipeline(this: Shell, p: Pipeline, io: Io): Promise<number> {
    this.checkTime();
    let input = io.stdin;
    let code = 0, failed = 0;
    const n = p.commands.length;
    const statuses: number[] = [];
    for (let k = 0; k < n; k++) {
      const last = k === n - 1;
      // shortcut: pipeline stages run one after another with buffered text; stream if huge outputs or infinite producers matter.
      const output = last ? io.out : new BufferWriter(this.options.maxStringBytes);
      const stage: Io = { ...io, stdin: input, out: output };
      if (n === 1) code = await this.execCommand(p.commands[k], stage);
      else if (last) {
        // shortcut: the last stage keeps its variables (bash's lastpipe), so `cmd | while read` loops can count; only `exit` is contained
        try { code = await this.execCommand(p.commands[k], stage); } catch (e) {
          if (!(e instanceof ExitSignal)) throw e;
          code = e.code;
        }
      } else {
        // every stage but the last is a subshell: `exit`, `cd` and assignments stay inside it
        const saved = this.saveState();
        try { code = await this.execCommand(p.commands[k], stage); } catch (e) {
          if (!(e instanceof ExitSignal)) throw e;
          code = e.code;
        } finally { this.restoreState(saved); }
      }
      if (code !== 0) failed = code;
      statuses.push(code);
      if (!last) input = new StdinBuffer((output as BufferWriter).toString());
    }
    this.pipeStatus = statuses;
    if (this.pipefail) code = failed;
    this.lastExitCode = code;
    return code;
  }

  // ---------- compound commands ----------

  /** @internal */ async execCommand(this: Shell, cmd: Command, io: Io): Promise<number> {
    if (++this.callDepth > 2000) throw new LimitExceeded('maximum nesting level exceeded');
    try {
      if (cmd.k === 'simple' && cmd.assigns.length === 0 && cmd.words.length === 1 && literal(cmd.words[0]) === 'exec')
        return await this.execRedirects(cmd.redirects, io);
      if (cmd.line) this.lineno = this.lineBase + cmd.line; // a failed redirection reports this line too
      const sinks: FileSink[] = [];
      const rio = cmd.redirects.length ? await this.applyRedirects(cmd.redirects, io, sinks) : io;
      if (!rio) return 1;
      let code: number;
      try {
        switch (cmd.k) {
          case 'simple': code = await this.execSimple(cmd, rio); break;
          case 'group': code = cmd.subshell ? await this.execSubshell(cmd.body, rio) : await this.execScript(cmd.body, rio); break;
          case 'if': code = await this.execIf(cmd, rio); break;
          case 'loop': code = await this.execLoop(cmd, rio); break;
          case 'for': code = cmd.select ? await this.execSelect(cmd, rio) : await this.execFor(cmd, rio); break;
          case 'arithfor': code = await this.execArithFor(cmd, rio); break;
          case 'case': code = await this.execCase(cmd, rio); break;
          case 'func': this.functions.set(cmd.name, { name: cmd.name, body: cmd.body, source: cmd.source });
            this.funcWhere.set(cmd.name, this.errWhere.at(-1)?.file ? this.errWhere.at(-1)!.name : 'environment');
            code = 0; break;
          case 'arith':
            code = (await this.evalArithExpr(cmd.expr, rio)) !== 0n ? 0 : 1;
            if (this.xtrace) rio.err.write(`+ (( ${cmd.expr.trim()} ))\n`);
            break;
          case 'cond':
            code = (await new CondEvaluator(this, cmd.items, rio).evaluate()) ? 0 : 1;
            break;
        }
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        rio.err.write(`${this.errPrefix}${e.message}\n`);
        code = cmd.k === 'cond' ? 2 : e.exitCode;
      } finally {
        for (const sink of sinks) {
          try { sink.flush(); } catch (e) {
            if (!(e instanceof ShellError)) throw e;
            io.err.write(`${this.errPrefix}${e.message}\n`);
            code = 1;
          }
        }
      }
      return code;
    } finally { this.callDepth--; }
  }

  /** @internal */ async execSubshell(this: Shell, body: Script, io: Io): Promise<number> {
    const saved = this.saveState();
    try { return await this.execScript(body, { ...io, out: new SwitchWriter(io.out), err: new SwitchWriter(io.err) }); } catch (e) {
      if (e instanceof ExitSignal) return e.code;
      throw e;
    } finally { this.restoreState(saved); }
  }

  /** @internal */ async condition(this: Shell, cond: Script, io: Io): Promise<number> {
    this.condDepth++;
    try { return await this.execScript(cond, io); } finally { this.condDepth--; }
  }

  /** @internal */ async execIf(this: Shell, c: Extract<Command, { k: 'if' }>, io: Io): Promise<number> {
    for (const [cond, body] of c.branches) if ((await this.condition(cond, io)) === 0) return this.execScript(body, io);
    return c.else ? this.execScript(c.else, io) : 0;
  }

  /** @internal Runs a loop body; returns [exit code, true if `break`]. */
  async loopBody(this: Shell, body: Script, io: Io): Promise<[number, boolean]> {
    this.loopDepth++;
    try { return [await this.execScript(body, io), false]; } catch (e) {
      if (!(e instanceof LoopControl)) throw e;
      if (e.levels > 1 && this.loopDepth > 1) throw new LoopControl(e.isBreak, e.levels - 1);
      return [0, e.isBreak];
    } finally { this.loopDepth--; }
  }

  /** @internal `break N` / `continue N`. */
  loopControl(this: Shell, isBreak: boolean, levels: number): never { throw new LoopControl(isBreak, levels); }
  /** @internal */ returnSignal(this: Shell, code: number): never { throw new ReturnSignal(code); }
  /** @internal */ exitSignal(this: Shell, code: number): never { throw new ExitSignal(code); }

  /** @internal */ checkIterations(this: Shell, counter: { n: number }) {
    this.checkTime();
    // nested loops multiply, so one tool call also has a total budget (10x the single-loop cap)
    const instead = 'process the whole input with one command (awk, sed, xargs) instead of a shell loop';
    const max = this.options.maxLoopIterations;
    if (++counter.n > max) throw new LimitExceeded(`loop aborted after ${max} iterations (the limit for one loop); ${instead}`);
    if (++this.totalIterations > max * 10)
      throw new LimitExceeded(`loop aborted after ${max * 10} iterations in this call (the limit for all loops in one call); ${instead}`);
  }

  /** @internal */ async execLoop(this: Shell, l: Extract<Command, { k: 'loop' }>, io: Io): Promise<number> {
    let code = 0;
    const counter = { n: 0 };
    for (;;) {
      if (((await this.condition(l.cond, io)) === 0) === l.until) break;
      this.checkIterations(counter); // counts body runs, so a loop of exactly the limit still finishes
      const [c, brk] = await this.loopBody(l.body, io);
      code = c;
      if (brk) break;
    }
    return code;
  }

  /** @internal */ async execFor(this: Shell, f: Extract<Command, { k: 'for' }>, io: Io): Promise<number> {
    const items = f.items === null ? [...this.positional] : await this.expandWords(f.items, io);
    let code = 0;
    const counter = { n: 0 };
    for (const item of items) {
      this.checkIterations(counter);
      this.setVar(f.name, item);
      const [c, brk] = await this.loopBody(f.body, io);
      code = c;
      if (brk) break;
    }
    return code;
  }

  /** @internal select: the menu and the PS3 prompt go to stderr, choices come from stdin, until `break` or end of input. */
  async execSelect(this: Shell, f: Extract<Command, { k: 'for' }>, io: Io): Promise<number> {
    const items = f.items === null ? [...this.positional] : await this.expandWords(f.items, io);
    let code = 0;
    const counter = { n: 0 };
    let menu = true;
    for (;;) {
      if (menu) items.forEach((item, k) => io.err.write(`${k + 1}) ${item}\n`));
      io.err.write(this.getVariable('PS3') || '#? ');
      const line = io.stdin.readLine();
      if (line === null) { io.err.write('\n'); break; }
      this.checkIterations(counter);
      menu = line.trim().length === 0;
      if (menu) continue; // an empty answer shows the menu again
      this.setVar('REPLY', line);
      const choice = /^\d+$/.test(line.trim()) ? Number(line.trim()) : 0;
      this.setVar(f.name, choice >= 1 && choice <= items.length ? items[choice - 1] : '');
      const [c, brk] = await this.loopBody(f.body, io);
      code = c;
      if (brk) break;
    }
    return code;
  }

  /** @internal */ async execArithFor(this: Shell, f: Extract<Command, { k: 'arithfor' }>, io: Io): Promise<number> {
    let code = 0;
    const counter = { n: 0 };
    if (f.init) await this.evalArithExpr(f.init, io);
    while (!f.cond || (await this.evalArithExpr(f.cond, io)) !== 0n) {
      this.checkIterations(counter);
      const [c, brk] = await this.loopBody(f.body, io);
      code = c;
      if (brk) break;
      if (f.step) await this.evalArithExpr(f.step, io);
    }
    return code;
  }

  /** @internal */ async execCase(this: Shell, c: Extract<Command, { k: 'case' }>, io: Io): Promise<number> {
    const subject = await this.expandSingle(c.subject, io);
    let code = 0;
    let fallThrough = false;
    for (const clause of c.clauses) {
      if (!fallThrough) {
        let matched = false;
        for (const pattern of clause.patterns)
          if ((matched = globRegex(await this.pattern(pattern, io), this.shopts.has('nocasematch'), true).test(subject))) break;
        if (!matched) continue;
      }
      code = await this.execScript(clause.body, io);
      if (clause.terminator === ';;') return code;
      fallThrough = clause.terminator === ';&'; // ;;& goes on testing the next patterns
    }
    return code;
  }
}

// ---------- conditional expressions ----------

/** Evaluates [[ ... ]]. */
export class CondEvaluator {
  private static readonly UNARY = new Set(['-z', '-n', '-e', '-f', '-d', '-s', '-r', '-w', '-x', '-L', '-h', '-a', '-v', '-o', '-t', '-b', '-c', '-p', '-S', '-g', '-u', '-k', '-O', '-G', '-N']);
  private static readonly BINARY = new Set(['==', '=', '!=', '=~', '<', '>', '-eq', '-ne', '-lt', '-le', '-gt', '-ge', '-nt', '-ot', '-ef']);
  private p = 0;
  constructor(private sh: Shell, private items: Word[], private io: Io) {}

  private isOp(s: string) { return this.p < this.items.length && literal(this.items[this.p]) === s; }
  private isBinary(w: Word) { const l = literal(w); return l !== null && CondEvaluator.BINARY.has(l); }

  async evaluate(): Promise<boolean> {
    if (this.items.length === 0) throw new ShellError(DIAGNOSTICS.conditionalSyntax, 2);
    const v = await this.or();
    if (this.p < this.items.length) throw new ShellError(diagnostic('conditionalSyntaxNear', { token: (literal(this.items[this.p]) ?? '') }), 2);
    return v;
  }
  private async or(): Promise<boolean> {
    let l = await this.and();
    while (this.isOp('||')) { this.p++; const r = await this.and(); l = l || r; }
    return l;
  }
  private async and(): Promise<boolean> {
    let l = await this.not();
    while (this.isOp('&&')) { this.p++; const r = await this.not(); l = l && r; }
    return l;
  }
  private async not(): Promise<boolean> {
    if (this.isOp('!')) { this.p++; return !(await this.not()); }
    return this.primary();
  }
  private async primary(): Promise<boolean> {
    const { items, sh, io } = this;
    if (this.isOp('(')) {
      this.p++;
      const v = await this.or();
      if (!this.isOp(')')) throw new ShellError(DIAGNOSTICS.conditionalParen, 2);
      this.p++;
      return v;
    }
    if (this.p >= items.length) throw new ShellError(DIAGNOSTICS.conditionalEnd, 2);
    const u = literal(items[this.p]);
    if (u !== null && CondEvaluator.UNARY.has(u) && this.p + 1 < items.length && !(this.p + 2 < items.length && this.isBinary(items[this.p + 1]))) {
      const arg = await sh.expandSingle(items[this.p + 1], io);
      this.p += 2;
      return testUnary(sh, u === '-a' ? '-e' : u === '-h' ? '-L' : u, arg);
    }
    const left = items[this.p++];
    if (this.p < items.length && this.isBinary(items[this.p])) {
      const op = literal(items[this.p++])!;
      if (this.p >= items.length) throw new ShellError(diagnostic('conditionalBinaryArgument', { op: op }), 2);
      const right = items[this.p++];
      const l = await sh.expandSingle(left, io);
      switch (op) {
        case '==': case '=': case '!=': {
          const match = globRegex(await sh.pattern(right, io), sh.shopts.has('nocasematch'), true).test(l);
          return op === '!=' ? !match : match;
        }
        case '=~': {
          // quoted parts of the regex match literally
          let reText = '';
          for (const part of right.parts) {
            const t = part.k === 'lit' ? part.text : await sh.expandSingle({ parts: [part] }, io);
            reText += part.q ? t.replace(/[\\^$.*+?()[\]{}|/]/g, '\\$&') : t;
          }
          let re: RegExp;
          try { re = new RegExp(posixToJs(reText, true), sh.shopts.has('nocasematch') ? 'i' : ''); } catch {
            throw new ShellError(`invalid regular expression \`${reText}'`, 2);
          }
          const m = re.exec(l);
          // BASH_REMATCH: the whole match, then each group; emptied when nothing matches
          const groups = new ShellArray(false);
          if (m) m.forEach((g, i) => groups.set(String(i), g ?? ''));
          sh.environment.delete('BASH_REMATCH');
          sh.arrays.set('BASH_REMATCH', groups);
          return m !== null;
        }
        case '<': return l < await sh.expandSingle(right, io);
        case '>': return l > await sh.expandSingle(right, io);
        case '-nt': return testNewer(sh, l, await sh.expandSingle(right, io));
        case '-ot': return testNewer(sh, await sh.expandSingle(right, io), l);
        case '-ef': return sh.agentFs.sameFile(sh.resolvePath(l), sh.resolvePath(await sh.expandSingle(right, io)));
        default: {
          const a = evalArith(sh, l), b = evalArith(sh, await sh.expandSingle(right, io));
          return op === '-eq' ? a === b : op === '-ne' ? a !== b : op === '-lt' ? a < b : op === '-le' ? a <= b : op === '-gt' ? a > b : a >= b;
        }
      }
    }
    return (await sh.expandSingle(left, io)).length > 0;
  }
}
