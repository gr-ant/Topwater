import type { Shell } from './shell.js';
import { ShellError, type VirtualFileSystem } from './vfs.js';
import { encodeText } from './bytes.js';

export { ShellError };

/** Where a command writes: stdout, stderr, a pipe, a file. */
export interface Writer { write(text: string): void }

export const nullWriter: Writer = { write() {} };

/** A safety limit was hit; it ends the whole tool call (exit 1 and a trailer). */
export class LimitExceeded extends Error {}

/** The call's time limit (or a `timeout` command's) passed. `owner` is the deadline that fired. */
export class DeadlineExceeded extends Error {
  constructor(public readonly owner: Deadline) { super('deadline exceeded'); }
}

/** A `sleep` reached the virtual deadline of an enclosing `timeout`; only that `timeout` catches it. */
export class VirtualTimeout extends DeadlineExceeded {
  constructor() { super({ at: -Infinity }); }
}

export interface Deadline { readonly at: number; readonly parent?: Deadline }

/** What a command returns: an exit code (void means 0). */
export type CommandResult = number | void;
export type CommandHandler = (ctx: CommandContext) => CommandResult | Promise<CommandResult>;

export interface RegisteredCommand {
  name: string;
  description: string;
  usage?: string;
  handler: CommandHandler;
}

/** A command's stdin. Shared by every command in a loop body so `while read` consumes it line by line. */
export class StdinBuffer {
  private pos = 0;
  /** False when the last readLine hit end of input before the delimiter. */
  lastTerminated = true;
  constructor(private text: string) {}
  reset(text: string) { this.text = text; this.pos = 0; }
  readAll(): string { const r = this.pos === 0 ? this.text : this.text.slice(this.pos); this.pos = this.text.length; return r; }
  readLine(delimiter = '\n'): string | null {
    if (this.pos >= this.text.length) return null;
    const nl = this.text.indexOf(delimiter, this.pos);
    const line = nl < 0 ? this.text.slice(this.pos) : this.text.slice(this.pos, nl);
    this.pos = nl < 0 ? this.text.length : nl + 1;
    this.lastTerminated = nl >= 0;
    return line;
  }
  /** read -n / -N: up to count characters, stopping after the delimiter unless it is null. */
  readChars(count: number, delimiter: string | null): string | null {
    if (this.pos >= this.text.length) return null;
    const end = Math.min(this.text.length, this.pos + count);
    let stop = -1;
    if (delimiter !== null) { const i = this.text.indexOf(delimiter, this.pos); if (i >= 0 && i < end) stop = i; }
    const chunk = this.text.slice(this.pos, stop >= 0 ? stop : end);
    this.pos = stop >= 0 ? stop + 1 : end;
    this.lastTerminated = stop >= 0 || chunk.length === count;
    return chunk;
  }
}

/** Everything a command needs: its arguments, stdio, the filesystem and the shell it runs in. */
export interface CommandContext {
  readonly shell: Shell;
  /** The name it was invoked as. */
  readonly name: string;
  readonly args: string[];
  readonly stdout: Writer;
  readonly stderr: Writer;
  readonly fs: VirtualFileSystem;
  /** Everything piped or redirected into the command ("" when nothing was). Reading it consumes stdin. */
  readonly stdinText: string;
  /** Reads one line of stdin (without the delimiter), or null at end of input. */
  readLine(delimiter?: string): string | null;
  /** Resolves a path against the shell's working directory. */
  resolvePath(path: string): string;
  /** Throws when the call's time limit (or an enclosing `timeout`) has passed; long-running loops call it now and then. */
  checkTime(): void;
  /** Milliseconds until the nearest deadline. */
  remainingMs(): number;
  /** Runs another command with this command's stdout/stderr (xargs, find -exec, timeout). */
  invoke(argv: string[], stdin?: string): Promise<number>;
  /** fds 3 and up given to this command (`read -u 3 x 3<file`). */
  readonly fds?: Map<number, Writer | StdinBuffer>;
  /** The stdin buffer itself (read, mapfile). */
  readonly stdin: StdinBuffer;
}

/** Small getopt-style parser shared by the commands. */
export class Args {
  readonly flags = new Set<string>();
  readonly values = new Map<string, string>();
  readonly positional: string[] = [];

  has(c: string) { return this.flags.has(c); }
  value(c: string): string | undefined { return this.values.get(c); }

  /**
   * @param flags allowed boolean short flags, e.g. "rfi"
   * @param valued short options that take a value, e.g. "n"
   * @param longs long-option aliases, e.g. "recursive=r,ignore-case=i"
   */
  static parse(args: readonly string[], flags = '', valued = '', longs = ''): Args {
    const map = new Map<string, string>();
    for (const pair of longs.split(',').filter(Boolean)) { const [k, v] = pair.split('='); map.set(k, v[0]); }
    const r = new Args();
    for (let i = 0; i < args.length; i++) {
      const a = args[i];
      if (a === '--') { r.positional.push(...args.slice(i + 1)); break; }
      if (a.startsWith('--')) {
        let name = a.slice(2);
        let inline: string | undefined;
        const eq = name.indexOf('=');
        if (eq > 0) { inline = name.slice(eq + 1); name = name.slice(0, eq); }
        const c = map.get(name);
        if (c === undefined) throw new ShellError(`unrecognized option '${a}'`, 2);
        if (valued.includes(c)) {
          if (inline === undefined && i + 1 >= args.length) throw new ShellError(`option '--${name}' requires an argument`, 2);
          r.values.set(c, inline ?? args[++i]);
        } else r.flags.add(c);
        continue;
      }
      if (a.length < 2 || a[0] !== '-') { r.positional.push(a); continue; }
      for (let k = 1; k < a.length; k++) {
        const c = a[k];
        if (valued.includes(c)) {
          if (k + 1 < a.length) r.values.set(c, a.slice(k + 1));
          else if (i + 1 < args.length) r.values.set(c, args[++i]);
          else throw new ShellError(`option requires an argument -- '${c}'`, 2);
          break;
        }
        if (!flags.includes(c)) throw new ShellError(`invalid option -- '${c}'`, 2);
        r.flags.add(c);
      }
    }
    return r;
  }

  /** Rewrites "-5" into "-n 5" (head/tail/xargs style). */
  static numericShorthand(args: readonly string[]): string[] {
    const result: string[] = [];
    for (const a of args) {
      const isValue = result.length > 0 && ['-n', '-c', '--lines', '--bytes'].includes(result[result.length - 1]);
      if (/^-\d+$/.test(a) && !isValue) result.push('-n', a.slice(1));
      else result.push(a);
    }
    return result;
  }
}

/** Splits text into lines, dropping the empty string after a final newline. */
export function splitLines(text: string): string[] {
  const lines = text.split('\n');
  if (lines.length > 0 && lines[lines.length - 1] === '') lines.pop();
  return lines;
}

/** The part of a ShellError message after its last ": " (the reason alone). */
export function reason(e: unknown): string {
  const m = e instanceof Error ? e.message : String(e);
  const i = m.lastIndexOf(': ');
  return i >= 0 ? m.slice(i + 2) : m;
}

/**
 * Each file argument's text (or stdin when there are none, or for "-"), reporting unreadable files to stderr as
 * `cmd: file: reason` and calling onError for them. The text is pipe text: binary bytes survive (see bytes.ts).
 */
export function* inputs(ctx: CommandContext, files: readonly string[], onError: () => void, quiet = false): Generator<[name: string, text: string]> {
  if (files.length === 0) { yield ['(standard input)', ctx.stdinText]; return; }
  for (const f of files) {
    if (f === '-') { yield ['(standard input)', ctx.stdinText]; continue; }
    let text: string;
    try { text = ctx.fs.readPipeText(ctx.resolvePath(f)); } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      if (!quiet) ctx.stderr.write(`${ctx.name}: ${f}: ${reason(e)}\n`);
      onError();
      continue;
    }
    yield [f, text];
  }
}

/** Like inputs, but the exact bytes (stdin's too). */
export function* inputBytes(ctx: CommandContext, files: readonly string[], onError: () => void): Generator<[name: string, data: Uint8Array]> {
  if (files.length === 0) { yield ['(standard input)', encodeText(ctx.stdinText)]; return; }
  for (const f of files) {
    if (f === '-') { yield ['(standard input)', encodeText(ctx.stdinText)]; continue; }
    let data: Uint8Array;
    try { data = ctx.fs.readBytes(ctx.resolvePath(f)); } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      ctx.stderr.write(`${ctx.name}: ${f}: ${reason(e)}\n`);
      onError();
      continue;
    }
    yield [f, data];
  }
}

/** "32 MB", "512 KB" or "1000 bytes": a limit as messages print it. */
export function sizeText(bytes: number): string {
  return bytes % (1 << 20) === 0 ? `${bytes / (1 << 20)} MB` : bytes % 1024 === 0 ? `${bytes / 1024} KB` : `${bytes} bytes`;
}

/** The string-limit error: `<what> too large (limit 32 MB); <instead>`. Ends the call. */
export function tooLarge(what: string, limit: number, instead: string): LimitExceeded {
  return new LimitExceeded(`${what} too large (limit ${sizeText(limit)}); ${instead}`);
}

/** Registers commands: the shape every command module exports (`register(shell)` calls `shell.register(...)`). */
export type Registrar = (shell: Shell) => void;
