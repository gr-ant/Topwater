// The types the shell and its parts share: options, the result of a call, Io, arrays and the control-flow signals. See docs/architecture.md.
import { ShellError, type VirtualFileSystem } from '../vfs.js';
import type { StdinBuffer, Writer } from '../command.js';
import type { Command } from '../parser.js';

export interface ShellOptions {
  /** The agent's user name (default "agent"); its home is /home/<user>. */
  user: string;
  hostname: string;
  /** Share a filesystem between shells. */
  fileSystem?: VirtualFileSystem;
  /** Upper bound on iterations of any one loop (all loops in a call: 10x). */
  maxLoopIterations: number;
  /** Most bytes one tar/unzip extraction or gzip decompression may produce. */
  maxExtractBytes: number;
  /** Most entries one tar or unzip run may extract or list. */
  maxExtractEntries: number;
  /** Longest one tool call may run, in ms. Past it the call stops with exit code 124 and a trailer. */
  maxCallDurationMs: number;
  /** Start of a virtual clock, ms since the Unix epoch (UTC): time then stands still and `sleep` advances it instantly. Default: the real clock. */
  startTime?: number;
  /** Seeds $RANDOM and mktemp names so a run repeats exactly. Default: unpredictable. Assigning RANDOM=n reseeds either way. */
  randomSeed?: number;
  /** Most bytes all files may hold together; the agent's writes past it fail with ENOSPC. */
  maxFileSystemBytes: number;
  /** Longest one string may be, and the most all variables may hold together (characters). */
  maxStringBytes: number;
}

export const DEFAULTS: ShellOptions = {
  user: 'agent', hostname: 'topwater', maxLoopIterations: 100_000, maxExtractBytes: 64 << 20, maxExtractEntries: 100_000,
  maxCallDurationMs: 120_000, maxFileSystemBytes: 128 << 20, maxStringBytes: 32 << 20,
};

/** What one call returned. */
export class ShellResult {
  constructor(
    public readonly stdout: string,
    public readonly stderr: string,
    public readonly exitCode: number,
    /** Set when a safety limit ended the call: a bracketed note that toToolOutput always shows last, never cut. */
    public readonly trailer?: string,
  ) {}

  /** The text to return to the model from the bash tool. */
  toToolOutput(maxChars = 30_000): string {
    // each stream is cut on its own, so a long stdout can't push the error message and exit code out of view
    const clip = (s: string, max: number) => (s.length <= max ? s : s.slice(0, max) + `\n... [output truncated, ${s.length - max} more characters]\n`);
    let text = clip(this.stdout, maxChars) + clip(this.stderr, Math.max(1, Math.floor(maxChars / 6)));
    const nl = () => (text.length === 0 || text.endsWith('\n') ? '' : '\n');
    if (this.exitCode !== 0) text += nl() + `[exit code ${this.exitCode}]`;
    if (this.trailer) text += nl() + this.trailer;
    return text.length === 0 ? '(no output)' : text;
  }
}

/** A command's stdio while it runs. fds 3 and up are only set when redirected for that command. */
export interface Io { stdin: StdinBuffer; out: Writer; err: Writer; fds?: Map<number, Writer | StdinBuffer> }

/** A bash array. Indexed arrays keep their elements in index order; associative ones in insertion order. */
export class ShellArray {
  items = new Map<string, string>();
  /** The index `+=(x)` appends at: one past the highest index ever set. */
  next = 0;
  private sorted = true;
  /** Which shell state owns this object; a (sub)shell copies an array before changing one it doesn't own. */
  epoch = 0;
  constructor(readonly assoc: boolean) {}

  get size() { return this.items.size; }
  get(key: string) { return this.items.get(key); }
  set(key: string, value: string) {
    if (!this.assoc && !this.items.has(key)) {
      const i = Number(key);
      if (i < this.next) this.sorted = false;
      this.next = Math.max(this.next, i + 1);
    }
    this.items.set(key, value);
  }
  delete(key: string) { this.items.delete(key); }
  keys(): string[] {
    if (!this.sorted) {
      this.items = new Map([...this.items].sort((a, b) => Number(a[0]) - Number(b[0])));
      this.sorted = true;
    }
    return [...this.items.keys()];
  }
  values(): string[] { this.keys(); return [...this.items.values()]; }
  clone(epoch: number): ShellArray {
    const c = new ShellArray(this.assoc);
    this.keys();
    c.items = new Map(this.items);
    c.next = this.next;
    c.epoch = epoch;
    return c;
  }
}

/** ${...} that is not a valid expansion. `ends`: a bad @ operator on a set variable, which ends the script (127);
 * any other drops the rest of its line with status 1. */
export class BadSubstitution extends ShellError { constructor(message: string, readonly ends = false) { super(message); } }

export class ControlFlow extends Error {}
/** Unwinds to the top level, which skips the rest of the current line (status 1), as bash does after a bad substitution. */
export class LineAbort extends ControlFlow {}
export class LoopControl extends ControlFlow { constructor(readonly isBreak: boolean, readonly levels: number) { super(); } }
export class ReturnSignal extends ControlFlow { constructor(readonly code: number) { super(); } }
export class ExitSignal extends ControlFlow { constructor(readonly code: number) { super(); } }

export interface FunctionDef { name: string; body: Command; source: string }

/** login: bash -l reads /etc/profile and the first of ~/.bash_profile, ~/.bash_login and ~/.profile first. */
export interface NewProcess { arg0: string; file?: string; setup?: () => void; login?: boolean }

export const EXECUTABLE_STUB = '\u007fELF\u0002\u0001\u0001\0\0\0\0\0\0\0\0\0';
