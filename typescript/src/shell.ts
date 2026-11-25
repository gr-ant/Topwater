import { VirtualFileSystem, ShellError, ordinal } from './vfs.js';
import {
  StdinBuffer, LimitExceeded, DeadlineExceeded, tooLarge, nullWriter, sizeText,
  type CommandContext, type CommandHandler, type RegisteredCommand, type Writer, type Deadline,
} from './command.js';
import {
  parse, parseWords, parseQuoted, parseParamWord, isName, literal, ParseError,
  type Script, type AndOr, type Pipeline, type Command, type Word, type WordPart, type Redirect, type Assignment,
} from './parser.js';
import { evalArith, type ArithHost } from './arith.js';
import { escapeGlob, hasWildcard, globRegex, globBody, cachedRegex, expandGlob } from './glob.js';
import { printable } from './bytes.js';
import { quoteValue, shellQuote, unescape } from './format.js';
import { posixToJs } from './regex.js';
import { registerAll } from './commands/index.js';
import { registerApi, type ApiEndpoint } from './api.js';
import { registerTools, mountMcp, type ToolSpec, type ToolInvoker, type McpClientLike, type McpMountOptions } from './tools.js';
import { testUnary, testNewer, attributeFlags } from './commands/builtins.js';

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
  /** Most bytes all files may hold together; the agent's writes past it fail with ENOSPC. */
  maxFileSystemBytes: number;
  /** Longest one string may be, and the most all variables may hold together (characters). */
  maxStringBytes: number;
}

// A JS regex match can't be interrupted, so an agent's catastrophically backtracking pattern (grep -E '(a|aa)+$') could
// hang the call. This V8 flag switches such a match to V8's linear-time engine (same results); patterns with
// backreferences have no linear engine and remain a risk. It is process-wide and only changes speed, never results.
// Node only (looked up at run time, so bundles for browsers carry no Node import); a browser host runs the shell in a
// Web Worker and terminates it at the time limit instead.
(globalThis as { process?: { getBuiltinModule?: (id: string) => { setFlagsFromString(f: string): void } | undefined } })
  .process?.getBuiltinModule?.('node:v8')?.setFlagsFromString('--enable-experimental-regexp-engine-on-excessive-backtracks');

const DEFAULTS: ShellOptions = {
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

class ControlFlow extends Error {}
class LoopControl extends ControlFlow { constructor(readonly isBreak: boolean, readonly levels: number) { super(); } }
export class ReturnSignal extends ControlFlow { constructor(readonly code: number) { super(); } }
export class ExitSignal extends ControlFlow { constructor(readonly code: number) { super(); } }

export interface FunctionDef { name: string; body: Command; source: string }

/** Buffers output (a pipeline stage, $(...), the call's stdout); past the string limit the call stops. */
export class BufferWriter implements Writer {
  protected chunks: string[] = [];
  length = 0;
  constructor(protected readonly limit: number) {}
  protected full(): void { throw tooLarge('output', this.limit, 'write to a file in smaller pieces or filter earlier in the pipeline'); }
  write(s: string) {
    if (!s) return;
    if (this.length + s.length > this.limit) this.full();
    this.chunks.push(s);
    this.length += s.length;
  }
  toString() {
    if (this.chunks.length > 1) this.chunks = [this.chunks.join('')];
    return this.chunks[0] ?? '';
  }
  clear() { this.chunks = []; this.length = 0; }
}

/** A redirect's file: buffered, but written through every megabyte, so the disk cap (not the string limit) bounds it. */
class FileSink extends BufferWriter {
  constructor(private readonly fs: VirtualFileSystem, readonly path: string) { super(1 << 20); }
  protected override full() { this.flush(); }
  flush() {
    const text = this.toString();
    this.clear(); // dropped even if the disk is full, so a loop writing to a full disk doesn't grow the buffer
    if (text) this.fs.appendText(this.path, text);
  }
  override write(s: string) {
    if (s.length < 1 << 20) { super.write(s); return; }
    this.flush(); // a big write goes straight to the file instead of through the buffer
    this.fs.appendText(this.path, s);
  }
}

/** A file opened by `exec >file`: written through, so later commands in the same call can read it. */
class FileAppender implements Writer {
  constructor(private readonly fs: VirtualFileSystem, private readonly path: string) {}
  write(s: string) { if (s) this.fs.appendText(this.path, s); }
}

/** A shell's stdout or stderr as `exec` sees it: what it points at can be changed for the rest of the shell. */
class SwitchWriter implements Writer {
  constructor(public target: Writer) {}
  write(s: string) { this.target.write(s); }
}

interface SavedState {
  cwd: string; env: Map<string, string>; aliases: Map<string, string>; functions: Map<string, FunctionDef>; positional: string[];
  errexit: boolean; pipefail: boolean; nounset: boolean; noglob: boolean; xtrace: boolean; noclobber: boolean; ifsUnset: boolean;
  shopts: Set<string>; traps: Map<string, string>; arrays: Map<string, ShellArray>; attrs: Map<string, string>; variableChars: number;
}

interface NewProcess { arg0: string; file?: string; setup?: () => void }

const BASH_VERSINFO = ['5', '2', '21', '1', 'release', 'x86_64-pc-linux-gnu'];
const SCRATCH = '__topwater_element';
export const EXECUTABLE_STUB = '\u007fELF\u0002\u0001\u0001\0\0\0\0\0\0\0\0\0';
const DAY = 86_400_000;
const PARAM_RE = /^([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[@*#?$!-])([^]*)$/;
const INDIRECT_RE = /^!([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[@*#?$!-])([^]*)$/;
const PREFIX_RE = /^!([A-Za-z_][A-Za-z0-9_]*)[*@]$/;

// bash builtins that have no program file on a real system: listing them in /bin makes a model suspicious
const NO_PROGRAM_FILE = new Set(['alias', 'break', 'cd', 'command', 'continue', 'declare', 'eval', 'exit', 'export', 'help',
  'history', 'let', 'local', 'mapfile', 'read', 'readarray', 'readonly', 'return', 'set', 'shift', 'shopt', 'source', 'trap', 'type', 'typeset', 'unalias', 'unset']);

/** ${p-w} ${p:-w} ${p=w} ${p:=w} ${p+w} ${p:+w} ${p?w} ${p:?w}: the operator and the word; null for any other operator. */
function defaultOp(rest: string): [op: string, word: string] | null {
  const n = rest[0] === ':' ? 2 : 1;
  return rest.length >= n && '-=+?'.includes(rest[n - 1]) ? [rest.slice(0, n), rest.slice(n)] : null;
}

/** Splits name[sub]rest (or !name[sub] for the keys); null when the text is not that shape. */
export function arrayRef(expr: string): { keys: boolean; name: string; sub: string; tail: string } | null {
  const start = expr.startsWith('!') ? 1 : 0, open = expr.indexOf('[');
  if (open <= start || !isName(expr.slice(start, open))) return null;
  let depth = 0, k = open;
  for (; k < expr.length; k++) {
    if (expr[k] === '[') depth++;
    else if (expr[k] === ']' && --depth === 0) break;
  }
  return k < expr.length ? { keys: start === 1, name: expr.slice(start, open), sub: expr.slice(open + 1, k), tail: expr.slice(k + 1) } : null;
}

const unquote = (s: string) => (s.length >= 2 && (s[0] === '"' || s[0] === "'") && s[s.length - 1] === s[0] ? s.slice(1, -1) : s);

/**
 * A simulated bash session. Give the model one "bash" tool (see BashTool) and pass its command to `run`.
 * Shell state (cwd, variables, functions, aliases, files) persists between calls.
 */
export class Shell implements ArithHost {
  readonly options: ShellOptions;
  /** The files, unrestricted: your app's view. */
  readonly fs: VirtualFileSystem;
  /** @internal The same files as the agent sees them; the shell and its commands work through this view. */
  readonly agentFs: VirtualFileSystem;
  /** Scalar variables. Entries the host puts here are exported to child processes. */
  environment = new Map<string, string>();
  aliases = new Map<string, string>();
  readonly history: string[] = [];
  lastExitCode = 0;

  /** @internal */ readonly commands = new Map<string, RegisteredCommand>();
  /** @internal */ functions = new Map<string, FunctionDef>();
  /** @internal */ arrays = new Map<string, ShellArray>();
  /** @internal declare attributes by variable name: i integer, r readonly, l/u case, n nameref, x exported. */
  varAttrs = new Map<string, string>();
  /** @internal */ positional: string[] = [];
  /** @internal */ errexit = false;
  /** @internal */ pipefail = false;
  /** @internal */ nounset = false;
  /** @internal */ noglob = false;
  /** @internal */ xtrace = false;
  /** @internal */ noclobber = false;
  /** @internal */ shopts = new Set(['cmdhist', 'complete_fullquote', 'extquote', 'force_fignore', 'globasciiranges', 'globskipdots',
    'hostcomplete', 'interactive_comments', 'patsub_replacement', 'progcomp', 'promptvars', 'sourcepath']);
  /** @internal */ traps = new Map<string, string>();
  /** @internal fds 3 and up opened by `exec 3>file` / `exec 3<file`. */
  readonly fds = new Map<number, Writer | StdinBuffer>();
  /** @internal */ readonly dirStack: string[] = [];
  /** @internal */ getoptsState = { optind: 0, pos: 0 };
  /** @internal */ sourceDepth = 0;
  /** @internal */ loopDepth = 0;
  /** @internal */ readonly exportedFunctions = new Set<string>();
  /** @internal */ readonly installedAt: number;

  private builtinNames: Set<string> | null = null;
  private readonly locals: Map<string, { value?: string; attrs?: string }>[] = [];
  private readonly localArrays: Map<string, ShellArray | undefined>[] = [];
  private readonly unexported = new Set<string>();
  private epoch = 0;
  private subshell = 0;
  private condDepth = 0;
  private cwdPath = '/';
  private ifsUnset = false;
  private lineno = 0;
  private lastBg = 0;
  private nextPid = 4300;
  private lastArg = '';
  private pipeStatus: number[] = [];
  private readonly funcNames: string[] = [];
  private readonly sources: string[] = [];
  private arg0 = 'bash';
  private readonly jobs = new Map<number, number>();
  private secondsBase = Date.now();
  private totalIterations = 0;
  private scriptDepth = 0;
  private arithDepth = 0;
  private variableChars = 0;
  private procSubCount = 0;
  private inErrTrap = false;
  private inDebugTrap = false;
  private callDepth = 0;
  /** @internal the innermost deadline (the call's, or a `timeout` command's) */
  deadline: Deadline = { at: Infinity };
  private readonly paramWords = new Map<string, Word>();

  constructor(options: Partial<ShellOptions> = {}) {
    this.options = { ...DEFAULTS, ...options };
    const fs = this.fs = options.fileSystem ?? new VirtualFileSystem();
    fs.capacity = this.options.maxFileSystemBytes;
    const user = this.options.user, home = '/home/' + user;
    this.agentFs = fs.as(user);
    const now = new Date();
    this.installedAt = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()) - 142 * DAY + (9 * 60 + 13) * 60_000;
    {
      for (const dir of [home, '/tmp', '/var/tmp', '/usr/bin', '/bin', '/usr/local/bin', '/opt', '/etc', '/dev/fd', '/var/log']) fs.mkdir(dir, true);
      this.seedSystemFiles(user, home);
      fs.mkdir('/root', true);
      fs.chmod('/root', 0o700);
      fs.setOwner(home, user, true);
      fs.chmod(home, 0o750);
      fs.chmod('/tmp', 0o1777);
      fs.chmod('/var/tmp', 0o1777);
      fs.chmod('/dev/null', 0o666);
    }
    for (const [k, v] of [['HOME', home], ['USER', user], ['LOGNAME', user], ['HOSTNAME', this.options.hostname], ['SHELL', '/bin/bash'],
      ['PATH', '/usr/local/bin:/usr/bin:/bin'], ['LANG', 'C.UTF-8'], ['TERM', 'xterm-256color']]) this.environment.set(k, v);
    this.cwd = home;
    registerAll(this);
    this.builtinNames = new Set(this.commands.keys());
    // the system was "installed" months ago; only the agent's home and the temp directories are fresh
    for (const dir of ['/bin', '/usr', '/etc', '/dev', '/opt', '/root', '/var/log', '/var/lib', '/var/cache', '/boot', '/lib', '/lib64', '/media', '/mnt', '/sbin', '/srv'])
      fs.setModified(dir, this.installedAt, true);
    for (const p of ['/', '/home', '/var', home + '/.bashrc', home + '/.profile', home + '/.bash_logout']) fs.setModified(p, this.installedAt);
  }

  /**
   * The ordinary clutter of a Linux install. A model that looks around and finds four files in /etc, no dotfiles
   * and no /proc starts wondering what kind of machine it is on, and spends its turns investigating.
   */
  private seedSystemFiles(user: string, home: string) {
    const fs = this.fs, host = this.options.hostname;
    for (const dir of ['/boot', '/lib', '/lib64', '/media', '/mnt', '/proc', '/run', '/sbin', '/srv', '/sys', '/usr/lib', '/usr/share', '/usr/sbin', '/var/lib', '/var/cache'])
      fs.mkdir(dir, true);
    const seed = (path: string, content: string) => { if (!fs.exists(path)) fs.writeText(path, content); };
    seed('/etc/os-release', 'PRETTY_NAME="Ubuntu 24.04.1 LTS"\nNAME="Ubuntu"\nVERSION_ID="24.04"\nVERSION="24.04.1 LTS (Noble Numbat)"\nVERSION_CODENAME=noble\nID=ubuntu\nID_LIKE=debian\nHOME_URL="https://www.ubuntu.com/"\n');
    fs.writeText('/etc/hostname', host + '\n');
    seed('/etc/issue', 'Ubuntu 24.04.1 LTS \\n \\l\n\n');
    seed('/etc/passwd', `root:x:0:0:root:/root:/bin/bash\n${user}:x:1000:1000:${user}:${home}:/bin/bash\n`);
    fs.writeText('/dev/null', '');
    seed('/etc/hosts', `127.0.0.1\tlocalhost\n127.0.1.1\t${host}\n\n::1\tip6-localhost ip6-loopback\nfe00::0\tip6-localnet\nff02::1\tip6-allnodes\nff02::2\tip6-allrouters\n`);
    seed('/etc/group', `root:x:0:\nadm:x:4:\nsudo:x:27:\nusers:x:100:\n${user}:x:1000:\n`);
    seed('/etc/shells', '# /etc/shells: valid login shells\n/bin/sh\n/bin/bash\n/usr/bin/bash\n/usr/bin/sh\n');
    seed('/etc/timezone', 'Etc/UTC\n');
    seed('/etc/resolv.conf', 'nameserver 127.0.0.53\noptions edns0 trust-ad\n');
    seed('/etc/lsb-release', 'DISTRIB_ID=Ubuntu\nDISTRIB_RELEASE=24.04\nDISTRIB_CODENAME=noble\nDISTRIB_DESCRIPTION="Ubuntu 24.04.1 LTS"\n');
    seed('/etc/fstab', '# <file system> <mount point> <type> <options> <dump> <pass>\n/dev/sda1\t/\text4\tdefaults\t0 1\n');
    // these agree with what uname, nproc, free, df and uptime print
    seed('/proc/version', 'Linux version 6.8.0-generic (buildd@lcy02-amd64-047) (x86_64-linux-gnu-gcc-13 (Ubuntu 13.2.0-23ubuntu4) 13.2.0, GNU ld (GNU Binutils for Ubuntu) 2.42) #1 SMP PREEMPT_DYNAMIC\n');
    seed('/proc/cpuinfo', [0, 1, 2, 3].map(i =>
      `processor\t: ${i}\nvendor_id\t: GenuineIntel\ncpu family\t: 6\nmodel\t\t: 85\nmodel name\t: Intel(R) Xeon(R) Platinum 8259CL CPU @ 2.50GHz\ncpu MHz\t\t: 2499.998\ncache size\t: 36608 KB\nphysical id\t: 0\nsiblings\t: 4\ncore id\t\t: ${i >> 1}\ncpu cores\t: 2\nbogomips\t: 4999.99\n\n`).join(''));
    seed('/proc/meminfo', 'MemTotal:        8014880 kB\nMemFree:         3984512 kB\nMemAvailable:    5748660 kB\nBuffers:          182272 kB\nCached:          2018752 kB\nSwapTotal:       2097148 kB\nSwapFree:        2097148 kB\nShmem:             12288 kB\n');
    seed('/proc/mounts', '/dev/sda1 / ext4 rw,relatime 0 0\nproc /proc proc rw,nosuid,nodev,noexec,relatime 0 0\nsysfs /sys sysfs rw,nosuid,nodev,noexec,relatime 0 0\ntmpfs /dev/shm tmpfs rw,nosuid,nodev 0 0\n');
    seed('/proc/uptime', '274320.42 1082431.77\n');
    seed('/proc/loadavg', '0.08 0.03 0.01 1/142 4242\n');
    seed(home + '/.profile', '# ~/.profile: executed by the command interpreter for login shells.\n\nif [ -n "$BASH_VERSION" ]; then\n    if [ -f "$HOME/.bashrc" ]; then\n\t. "$HOME/.bashrc"\n    fi\nfi\n\nif [ -d "$HOME/bin" ] ; then\n    PATH="$HOME/bin:$PATH"\nfi\n');
    seed(home + '/.bash_logout', '# ~/.bash_logout: executed by bash(1) when login shell exits.\n\nif [ "$SHLVL" = 1 ]; then\n    [ -x /usr/bin/clear_console ] && /usr/bin/clear_console -q\nfi\n');
    seed(home + '/.bashrc', "# ~/.bashrc: executed by bash(1) for non-login shells.\n\n# If not running interactively, don't do anything\ncase $- in\n    *i*) ;;\n      *) return;;\nesac\n\nHISTCONTROL=ignoreboth\nHISTSIZE=1000\nHISTFILESIZE=2000\n\nshopt -s checkwinsize\n\nPS1='\\u@\\h:\\w\\$ '\n\nalias ls='ls --color=auto'\nalias ll='ls -alF'\nalias la='ls -A'\nalias l='ls -CF'\n");
  }

  get cwd(): string { return this.cwdPath; }
  set cwd(value: string) {
    const abs = this.resolvePath(value);
    if (!this.fs.isDirectory(abs)) throw new ShellError(`${value}: No such file or directory`);
    this.cwdPath = abs;
    this.environment.set('PWD', abs);
  }

  resolvePath(path: string): string { return VirtualFileSystem.normalize(this.cwdPath, path); }

  // ---------- registration ----------

  /** Adds a command the agent can run (and lists it in /usr/bin and `help`). Returns this shell. */
  register(name: string, handler: CommandHandler, description = '', usage?: string): this {
    this.commands.set(name, { name, description, usage, handler });
    this.builtinNames?.delete(name); // an app command replacing a built-in owns its own --help/--version
    const shellOnly = this.builtinNames === null && NO_PROGRAM_FILE.has(name);
    if (!shellOnly && !name.includes('/') && name !== '.' && name !== '..' && name !== ':' && name !== '[' && this.fs.isDirectory('/usr/bin')) {
      {
        for (const dir of ['/usr/bin/', '/bin/']) {
          if (!this.fs.isDirectory(dir)) continue;
          this.fs.writeText(dir + name, EXECUTABLE_STUB);
          this.fs.chmod(dir + name, 0o755);
          this.fs.setModified(dir + name, this.installedAt);
          // shortcut: a stub is 16 bytes but reports a plausible program size; `wc -c` on it would disagree
          let h = 17;
          for (const c of name) h = (Math.imul(h, 31) + c.charCodeAt(0)) >>> 0;
          this.fs.setReportedSize(dir + name, 14_000 + Math.floor((h % 180_000) / 8) * 8);
          this.fs.setModified(dir, this.installedAt);
        }
      }
    }
    return this;
  }

  /** Exposes schema-described tools as one program: `program TOOL --option value` calls `call` (see tools.ts). */
  registerTools(program: string, tools: ToolSpec[], call: ToolInvoker, description?: string): this {
    registerTools(this, program, tools, call, description);
    return this;
  }

  /** Mounts a connected MCP server (e.g. an @modelcontextprotocol/sdk Client) as one program; returns the tool names exposed. */
  mountMcp(program: string, client: McpClientLike, options?: McpMountOptions): Promise<string[]> {
    return mountMcp(this, program, client, options);
  }

  /** Lets the agent call real HTTP APIs with credentials it never sees: `program get ALIAS /path` (see api.ts). */
  registerApi(program: string, endpoints: Record<string, ApiEndpoint>, fetchImpl?: typeof fetch): this {
    registerApi(this, program, endpoints, fetchImpl);
    return this;
  }

  unregister(name: string): boolean {
    for (const dir of ['/usr/bin/', '/bin/']) if (this.fs.isFile(dir + name)) this.fs.delete(dir + name);
    return this.commands.delete(name);
  }

  // ---------- execution ----------

  /** Runs one tool call: the model's command line. Never throws for anything the command does. */
  async run(commandLine: string): Promise<ShellResult> {
    this.history.push(commandLine);
    const stdout = new BufferWriter(this.options.maxStringBytes);
    const stderr = new BufferWriter(this.options.maxStringBytes);
    let code: number;
    let trailer: string | undefined;
    this.totalIterations = this.scriptDepth = this.callDepth = this.arithDepth = 0;
    const callDeadline: Deadline = { at: Date.now() + this.options.maxCallDurationMs };
    this.deadline = callDeadline;
    const io: Io = { stdin: new StdinBuffer(''), out: new SwitchWriter(stdout), err: new SwitchWriter(stderr) };
    const raw = (s: string) => { stderr.clear; (stderr as unknown as { chunks: string[] }).chunks.push(s); stderr.length += s.length; };
    try {
      // shortcut: EXIT traps fire when the tool call ends (like `set -e`, they last one call), not at logout
      try { code = await this.execScript(parse(commandLine), io); } finally { await this.runExitTrap(io); }
    } catch (e) {
      if (e instanceof ParseError) { raw(`bash: ${e.message}\n`); code = 2; }
      else if (e instanceof ExitSignal) code = e.code;
      else if (e instanceof ReturnSignal) code = e.code;
      else if (e instanceof LoopControl) code = 0;
      else if (e instanceof LimitExceeded) { raw(`bash: ${e.message}\n`); code = 1; trailer = stopped(e.message); }
      else if (e instanceof DeadlineExceeded) {
        code = 124; // what `timeout` exits with
        trailer = stopped(`this call exceeded the ${seconds(this.options.maxCallDurationMs)} time limit; split the work into shorter calls`);
      } else if (e instanceof RangeError && /call stack/i.test(e.message)) {
        const msg = 'maximum nesting level exceeded';
        raw(`bash: ${msg}\n`); code = 1; trailer = stopped(msg);
      } else { // an interpreter bug must not take down the host's agent loop
        raw(`bash: internal error: ${e instanceof Error ? `${e.name}: ${e.message}` : String(e)}\n`);
        code = 1;
      }
    } finally {
      // shortcut: `set -e` / `set -o pipefail` last for one tool call, so a stray `set -e` can't poison the session.
      this.errexit = this.pipefail = this.nounset = this.noglob = this.xtrace = this.noclobber = false;
      this.fds.clear();
      this.deadline = { at: Infinity };
      if (this.procSubCount > 0 && this.fs.isDirectory('/dev/fd'))
        for (const f of this.fs.list('/dev/fd')) this.fs.delete('/dev/fd/' + f, true);
      this.procSubCount = 0;
      this.locals.length = this.localArrays.length = this.funcNames.length = 0;
    }
    this.lastExitCode = code;
    return new ShellResult(printable(stdout.toString()), printable(stderr.toString()), code, trailer);
  }

  /** @internal Called before a variable or element takes a value of `adding` characters in place of `replacing`. */
  chargeVariable(name: string, adding: number, replacing: number) {
    const max = this.options.maxStringBytes;
    if (adding > max) throw tooLarge(`${name}: value`, max, 'write the data to a file instead');
    if ((this.variableChars += adding) <= max) return;
    let total = 0;
    for (const [k, v] of this.environment) total += k.length + v.length;
    for (const a of this.arrays.values()) for (const [k, v] of a.items) total += k.length + v.length;
    this.variableChars = total - replacing + adding;
    if (this.variableChars > max) throw tooLarge('variables in total', max, 'keep big data in files and unset variables you no longer need');
  }

  /** @internal Throws when the call's time limit (or a `timeout`) has passed. */
  checkTime() {
    const now = Date.now();
    let owner: Deadline | undefined;
    for (let d: Deadline | undefined = this.deadline; d; d = d.parent) if (now > d.at) owner = d; // the outermost one that passed wins
    if (owner) throw new DeadlineExceeded(owner);
  }

  /** @internal */
  remainingMs(): number {
    let min = Infinity;
    for (let d: Deadline | undefined = this.deadline; d; d = d.parent) min = Math.min(min, d.at);
    return min - Date.now();
  }

  /** @internal Runs shell text: `eval`, `source`, traps, `bash -c`, scripts. */
  async runScriptText(text: string, io: Io, subshell: boolean, positional?: string[], process?: NewProcess, sourced?: string): Promise<number> {
    let script: Script;
    try { script = parse(text); } catch (e) {
      if (e instanceof ParseError) { io.err.write(`bash: ${e.message}\n`); return 2; }
      throw e;
    }
    if (++this.scriptDepth > 100) throw new LimitExceeded('maximum script nesting level exceeded (100)');
    const savedArg0 = this.arg0;
    const sources = this.sources.length;
    const file = process?.file ?? sourced;
    if (file !== undefined) this.sources.push(file);
    try {
      if (!subshell) {
        const savedPos = this.positional;
        if (positional) this.positional = positional;
        try { return await this.execScript(script, io); } finally { if (positional) this.positional = savedPos; }
      }
      const saved = this.saveState();
      this.traps = new Map();
      io = { ...io, out: new SwitchWriter(io.out), err: new SwitchWriter(io.err) }; // `exec >file` in a script ends with it
      if (process) this.becomeNewProcess(process);
      if (positional) this.positional = positional;
      try {
        try { return await this.execScript(script, io); } finally { await this.runExitTrap(io); }
      } catch (e) {
        if (e instanceof ExitSignal) return e.code;
        throw e;
      } finally { this.restoreState(saved); }
    } finally {
      this.scriptDepth--;
      this.arg0 = savedArg0;
      this.sources.length = sources;
    }
  }

  /** What a child bash inherits: exported variables (not their other attributes) and exported functions. */
  private becomeNewProcess(process: NewProcess) {
    this.environment = new Map([...this.environment].filter(([k]) => this.isExported(k)));
    this.varAttrs = new Map([...this.environment.keys()].map(k => [k, 'x']));
    this.arrays = new Map();
    this.aliases = new Map();
    this.functions = new Map([...this.functions].filter(([k]) => this.exportedFunctions.has(k)));
    this.errexit = this.pipefail = this.nounset = this.xtrace = this.noglob = this.noclobber = false;
    this.arg0 = process.arg0;
    process.setup?.();
  }

  private async runExitTrap(io: Io) {
    const action = this.traps.get('EXIT');
    this.traps.delete('EXIT');
    if (!action) return;
    const saved = this.lastExitCode;
    try { await this.runScriptText(action, io, false); } catch (e) { if (!(e instanceof ControlFlow)) throw e; }
    this.lastExitCode = saved;
  }

  // arrays are shared with the saved state and copied on first write (see writable)
  private saveState(): SavedState {
    this.epoch++;
    this.subshell++;
    return {
      cwd: this.cwdPath, env: new Map(this.environment), aliases: new Map(this.aliases), functions: new Map(this.functions),
      positional: this.positional, errexit: this.errexit, pipefail: this.pipefail, nounset: this.nounset, noglob: this.noglob,
      xtrace: this.xtrace, noclobber: this.noclobber, ifsUnset: this.ifsUnset, shopts: new Set(this.shopts), traps: new Map(this.traps),
      arrays: new Map(this.arrays), attrs: new Map(this.varAttrs), variableChars: this.variableChars,
    };
  }

  private restoreState(s: SavedState) {
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
    this.cwdPath = this.agentFs.isDirectory(s.cwd) ? s.cwd : '/';
  }

  private async execScript(script: Script, io: Io): Promise<number> {
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

  private async execListPipeline(p: Pipeline, condition: boolean, io: Io): Promise<number> {
    if (!condition) return this.execPipeline(p, io);
    this.condDepth++;
    try { return await this.execPipeline(p, io); } finally { this.condDepth--; }
  }

  /**
   * cmd &: runs now, to completion, in a subshell; $! gets a job id that `wait` reports its status for.
   * shortcut: jobs run in order, not concurrently; run them truly in parallel if agents start relying on overlap.
   */
  private async execBackground(item: AndOr, io: Io): Promise<number> {
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
  waitFor(pid: number): number { const c = this.jobs.get(pid); this.jobs.delete(pid); return c ?? 127; }
  /** @internal `wait -n`: the status of the oldest finished job, or 127 when there are none. */
  waitNext(): number { return this.jobs.size === 0 ? 127 : this.waitFor(Math.min(...this.jobs.keys())); }
  /** @internal */ isJob(pid: number) { return this.jobs.has(pid); }
  /** @internal */ forgetJobs() { this.jobs.clear(); }
  /** @internal */ jobList(): number[] { return [...this.jobs.keys()]; }

  private async execPipeline(p: Pipeline, io: Io): Promise<number> {
    if (p.timed) {
      const start = Date.now();
      const status = p.commands.length === 0 ? 0 : await this.execPipeline({ ...p, timed: false }, io);
      const t = (Date.now() - start) / 1000;
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

  private async runPipeline(p: Pipeline, io: Io): Promise<number> {
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

  private async execCommand(cmd: Command, io: Io): Promise<number> {
    if (++this.callDepth > 2000) throw new LimitExceeded('maximum nesting level exceeded');
    try {
      if (cmd.k === 'simple' && cmd.assigns.length === 0 && cmd.words.length === 1 && literal(cmd.words[0]) === 'exec')
        return await this.execRedirects(cmd.redirects, io);
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
          case 'func': this.functions.set(cmd.name, { name: cmd.name, body: cmd.body, source: cmd.source }); code = 0; break;
          case 'arith':
            this.lineno = cmd.line;
            code = (await this.evalArithExpr(cmd.expr, rio)) !== 0n ? 0 : 1;
            if (this.xtrace) rio.err.write(`+ (( ${cmd.expr.trim()} ))\n`);
            break;
          case 'cond':
            this.lineno = cmd.line;
            code = (await new CondEvaluator(this, cmd.items, rio).evaluate()) ? 0 : 1;
            break;
        }
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        rio.err.write(`bash: ${e.message}\n`);
        code = cmd.k === 'cond' ? 2 : e.exitCode;
      } finally {
        for (const sink of sinks) {
          try { sink.flush(); } catch (e) {
            if (!(e instanceof ShellError)) throw e;
            io.err.write(`bash: ${e.message}\n`);
            code = 1;
          }
        }
      }
      return code;
    } finally { this.callDepth--; }
  }

  private async execSubshell(body: Script, io: Io): Promise<number> {
    const saved = this.saveState();
    try { return await this.execScript(body, { ...io, out: new SwitchWriter(io.out), err: new SwitchWriter(io.err) }); } catch (e) {
      if (e instanceof ExitSignal) return e.code;
      throw e;
    } finally { this.restoreState(saved); }
  }

  private async condition(cond: Script, io: Io): Promise<number> {
    this.condDepth++;
    try { return await this.execScript(cond, io); } finally { this.condDepth--; }
  }

  private async execIf(c: Extract<Command, { k: 'if' }>, io: Io): Promise<number> {
    for (const [cond, body] of c.branches) if ((await this.condition(cond, io)) === 0) return this.execScript(body, io);
    return c.else ? this.execScript(c.else, io) : 0;
  }

  /** Runs a loop body; returns [exit code, true if `break`]. */
  private async loopBody(body: Script, io: Io): Promise<[number, boolean]> {
    this.loopDepth++;
    try { return [await this.execScript(body, io), false]; } catch (e) {
      if (!(e instanceof LoopControl)) throw e;
      if (e.levels > 1 && this.loopDepth > 1) throw new LoopControl(e.isBreak, e.levels - 1);
      return [0, e.isBreak];
    } finally { this.loopDepth--; }
  }

  /** @internal `break N` / `continue N`. */
  loopControl(isBreak: boolean, levels: number): never { throw new LoopControl(isBreak, levels); }
  /** @internal */ returnSignal(code: number): never { throw new ReturnSignal(code); }
  /** @internal */ exitSignal(code: number): never { throw new ExitSignal(code); }

  private checkIterations(counter: { n: number }) {
    this.checkTime();
    // nested loops multiply, so one tool call also has a total budget (10x the single-loop cap)
    const instead = 'process the whole input with one command (awk, sed, xargs) instead of a shell loop';
    const max = this.options.maxLoopIterations;
    if (++counter.n > max) throw new LimitExceeded(`loop aborted after ${max} iterations (the limit for one loop); ${instead}`);
    if (++this.totalIterations > max * 10)
      throw new LimitExceeded(`loop aborted after ${max * 10} iterations in this call (the limit for all loops in one call); ${instead}`);
  }

  private async execLoop(l: Extract<Command, { k: 'loop' }>, io: Io): Promise<number> {
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

  private async execFor(f: Extract<Command, { k: 'for' }>, io: Io): Promise<number> {
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

  /** select: the menu and the PS3 prompt go to stderr, choices come from stdin, until `break` or end of input. */
  private async execSelect(f: Extract<Command, { k: 'for' }>, io: Io): Promise<number> {
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

  private async execArithFor(f: Extract<Command, { k: 'arithfor' }>, io: Io): Promise<number> {
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

  private async execCase(c: Extract<Command, { k: 'case' }>, io: Io): Promise<number> {
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

  // ---------- redirections ----------

  private async applyRedirects(redirects: Redirect[], io: Io, sinks: FileSink[]): Promise<Io | null> {
    for (const r of redirects) {
      try {
        if (r.op === '<<' || r.op === '<<<' || r.op === '<') {
          const input = await this.openInput(r, io);
          io = r.fd >= 3 ? { ...io, fds: new Map(io.fds ?? []).set(r.fd, input) } : r.fd === 0 ? { ...io, stdin: input } : io;
        } else if (r.op === '>&' || r.op === '<&') {
          const target = await this.expandSingle(r.target!, io);
          if (target !== '-' && !/^\d+$/.test(target) && r.op === '>&') { // >&file is &>file
            const w = await this.openOutput({ ...r, op: '&>' }, io, path => { const s = new FileSink(this.agentFs, path); sinks.push(s); return s; });
            io = { ...io, out: w, err: w };
            continue;
          }
          let dup: Writer | StdinBuffer;
          if (target === '-') dup = r.op === '<&' ? new StdinBuffer('') : nullWriter;
          else {
            const d = /^\d+$/.test(target) ? this.descriptor(Number(target), io) : undefined;
            if (!d) throw new ShellError(`${target}: ${/^\d+$/.test(target) ? 'Bad file descriptor' : 'ambiguous redirect'}`);
            dup = d;
          }
          io = withFd(io, r.fd, dup);
        } else {
          const w = await this.openOutput(r, io, path => { const s = new FileSink(this.agentFs, path); sinks.push(s); return s; });
          io = r.op.startsWith('&') ? { ...io, out: w, err: w } : withFd(io, r.fd, w);
        }
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        io.err.write(`bash: ${e.message}\n`);
        return null;
      }
    }
    return io;
  }

  private async openInput(r: Redirect, io: Io): Promise<StdinBuffer> {
    if (r.op === '<<') return new StdinBuffer(await this.expandSingle(r.here!.body, io));
    if (r.op === '<<<') return new StdinBuffer((await this.expandSingle(r.target!, io)) + '\n');
    const target = await this.expandSingle(r.target!, io);
    const path = this.resolvePath(target);
    if (path === '/dev/stdin') return io.stdin;
    try { return new StdinBuffer(this.agentFs.readPipeText(path)); } catch (e) {
      if (e instanceof ShellError) throw new ShellError(`${target}: ${e.message.slice(e.message.lastIndexOf(': ') + 2)}`);
      throw e;
    }
  }

  /** The writer for > >> >| &> &>>: /dev/null, /dev/stdout, /dev/stderr, or a file made by `open`. */
  private async openOutput(r: Redirect, io: Io, open: (path: string) => Writer): Promise<Writer> {
    const target = await this.expandSingle(r.target!, io);
    const path = this.resolvePath(target);
    if (path === '/dev/null') return nullWriter;
    if (path === '/dev/stderr') return io.err;
    if (path === '/dev/stdout') return io.out;
    if (path.startsWith('/dev/fd/') && /^\d+$/.test(path.slice(8))) {
      const d = this.descriptor(Number(path.slice(8)), io);
      if (d && !(d instanceof StdinBuffer)) return d;
    }
    if (target === '') throw new ShellError(': No such file or directory');
    if (this.agentFs.isDirectory(path)) throw new ShellError(`${target}: Is a directory`);
    if (this.noclobber && (r.op === '>' || r.op === '&>') && this.agentFs.isFile(path)) throw new ShellError(`${target}: cannot overwrite existing file`);
    try {
      if (!r.op.endsWith('>>') || !this.agentFs.exists(path)) this.agentFs.writeText(path, ''); // truncate on open, like bash
    } catch (e) {
      if (e instanceof ShellError) throw new ShellError(`${target}: ${e.message.slice(e.message.lastIndexOf(': ') + 2)}`);
      throw e;
    }
    return open(path);
  }

  /** What fd n refers to: 0 a StdinBuffer, 1 and 2 writers, 3+ whatever `exec` or this command opened. */
  private descriptor(n: number, io: Io): Writer | StdinBuffer | undefined {
    if (n === 0) return io.stdin;
    if (n === 1) return io.out;
    if (n === 2) return io.err;
    return io.fds?.get(n) ?? this.fds.get(n);
  }

  /**
   * `exec` with only redirections: they last for the rest of the shell. fds 1 and 2 are retargeted through the
   * SwitchWriter each (sub)shell starts with; files opened this way are written through as text arrives.
   */
  private async execRedirects(redirects: Redirect[], io: Io): Promise<number> {
    for (const r of redirects) {
      try {
        let target: Writer | StdinBuffer | null;
        if (r.op === '<<' || r.op === '<<<' || r.op === '<') target = await this.openInput(r, io);
        else if (r.op === '>&' || r.op === '<&') {
          const t = await this.expandSingle(r.target!, io);
          if (t === '-') target = null;
          else {
            const d = /^\d+$/.test(t) ? this.descriptor(Number(t), io) : undefined;
            if (!d) throw new ShellError(`${t}: Bad file descriptor`);
            target = d instanceof SwitchWriter ? d.target : d;
          }
        } else target = await this.openOutput(r, io, path => new FileAppender(this.agentFs, path));
        let named = r.fd;
        if (r.fdVar !== undefined) {
          if (target === null) named = /^\d+$/.test(this.getVariable(r.fdVar)) ? Number(this.getVariable(r.fdVar)) : -1; // {fd}>&- closes it
          else { named = 10; while (this.fds.has(named)) named++; this.setVar(r.fdVar, String(named)); }
        }
        for (const fd of r.op.startsWith('&') ? [1, 2] : [named]) {
          if (fd < 0) continue;
          if (fd === 0) io.stdin.reset(target instanceof StdinBuffer ? target.readAll() : '');
          else if (fd <= 2) {
            const w = fd === 1 ? io.out : io.err;
            if (w instanceof SwitchWriter) w.target = target && !(target instanceof StdinBuffer) ? target : nullWriter;
          } else if (target === null) this.fds.delete(fd);
          else this.fds.set(fd, target);
        }
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        io.err.write(`bash: ${e.message}\n`);
        return 1;
      }
    }
    return 0;
  }

  // ---------- simple commands ----------

  private async execSimple(c: Extract<Command, { k: 'simple' }>, io: Io): Promise<number> {
    this.lineno = c.line;
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
        if (e instanceof ShellError && words.length === 0 && e.message.endsWith('readonly variable')) {
          io.err.write(`bash: ${e.message}\n`);
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

  /** `local x="a b" y=$(cmd)`: arguments that look like assignments are expanded as assignments (no splitting). */
  private async expandDeclaration(words: Word[], io: Io): Promise<string[]> {
    const argv: string[] = [];
    for (const w of words) {
      const a = w.assign;
      if (argv.length > 0 && a && a.elements === undefined && a.index === undefined)
        argv.push(a.name + (a.append ? '+=' : '=') + await this.expandAssignment(a.value, io));
      else argv.push(...await this.expandWords([w], io));
    }
    return argv;
  }

  /** @internal Runs argv: a function, a script file, or a registered command. `skipFunctions`: `command ls` / `builtin cd`. */
  async invoke(argv: string[], io: Io, skipFunctions = false): Promise<number> {
    let name = argv[0];
    const fn = skipFunctions ? undefined : this.functions.get(name);
    if (fn) return this.callFunction(fn.body, argv, io);
    if (name.includes('/')) {
      const path = this.resolvePath(name);
      const dir = VirtualFileSystem.getParent(path);
      if ((dir === '/usr/bin' || dir === '/bin' || dir === '/usr/local/bin') && this.commands.has(VirtualFileSystem.getName(path)))
        name = VirtualFileSystem.getName(path);
      else return this.runScriptFile(name, path, argv, io);
    }
    const cmd = this.commands.get(name);
    if (!cmd) {
      io.err.write(`${argv[0]}: command not found (run 'help' to list available commands)\n`);
      return 127;
    }
    const ctx = this.makeContext(name, argv.slice(1), io);
    if (argv.length === 2 && this.builtinNames?.has(name) && standardOption(ctx, cmd, argv[1])) return 0;
    try {
      const r = await cmd.handler(ctx);
      return typeof r === 'number' ? r : 0;
    } catch (e) {
      if (e instanceof ControlFlow || e instanceof LimitExceeded || e instanceof DeadlineExceeded) throw e;
      if (e instanceof ShellError) { io.err.write(`${name}: ${e.message}\n`); return e.exitCode; }
      if (e instanceof RangeError && /call stack/i.test(e.message)) throw new LimitExceeded('maximum nesting level exceeded');
      io.err.write(`${name}: ${e instanceof Error ? e.message : String(e)}\n`);
      return 1;
    }
  }

  /** @internal */
  makeContext(name: string, args: string[], io: Io): CommandContext {
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

  private async runScriptFile(name: string, path: string, argv: string[], io: Io): Promise<number> {
    if (this.agentFs.isDirectory(path)) { io.err.write(`bash: ${name}: Is a directory\n`); return 126; }
    if (!this.agentFs.isFile(path)) { io.err.write(`bash: ${name}: ${this.agentFs.whyMissing(path)}\n`); return this.agentFs.whyMissing(path) === 'Permission denied' ? 126 : 127; }
    if (!this.agentFs.canExecute(path) || !this.agentFs.canRead(path)) { io.err.write(`bash: ${name}: Permission denied\n`); return 126; }
    const text = this.agentFs.readText(path);
    if (text.startsWith(EXECUTABLE_STUB.slice(0, 4))) { io.err.write(`bash: ${name}: cannot execute binary file\n`); return 126; }
    if (text.startsWith('#!')) {
      const nl = text.indexOf('\n');
      const interp = text.slice(2, nl >= 0 ? nl : text.length).trim();
      if (!/(^|\/|\s)(ba)?sh(\s|$)/.test(interp)) {
        io.err.write(`bash: ${name}: ${interp.split(' ')[0]}: bad interpreter: No such file or directory\n`);
        return 126;
      }
    }
    return this.runScriptText(text, io, true, argv.slice(1), { arg0: name, file: name });
  }

  private async callFunction(body: Command, argv: string[], io: Io): Promise<number> {
    if (this.locals.length >= 200) throw new LimitExceeded(`${argv[0]}: maximum function nesting level exceeded (200)`);
    const savedPos = this.positional;
    const onReturn = this.traps.get('RETURN');
    this.positional = argv.slice(1);
    this.locals.push(new Map());
    this.localArrays.push(new Map());
    this.funcNames.push(argv[0]);
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
    }
    // a RETURN trap set inside the function runs as it returns
    const trap = this.traps.get('RETURN');
    if (trap && trap !== onReturn) {
      this.traps.delete('RETURN');
      await this.runScriptText(trap, io, false);
    }
    return this.lastExitCode = code;
  }

  /** @internal */ get inFunction() { return this.locals.length > 0; }
  /** @internal */ get funcNameStack(): readonly string[] { return this.funcNames; }

  /** @internal [[ -v name ]], [[ -v a[k] ]]: whether the variable or element is set. */
  isVariableSet(expr: string): boolean {
    const r = arrayRef(expr);
    if (r && !r.keys && r.tail === '') {
      if (r.sub === '@' || r.sub === '*') return (this.getArray(r.name)?.size ?? 0) > 0;
      return this.getElement(r.name, r.sub) !== null;
    }
    return isName(expr) || /^\d+$/.test(expr) ? this.isSet(expr) : false;
  }

  /** @internal The set -o options this shell has, and whether each is on. */
  get optionList(): [string, boolean][] {
    return [['allexport', false], ['braceexpand', true], ['emacs', false], ['errexit', this.errexit], ['errtrace', false], ['functrace', false],
      ['hashall', true], ['histexpand', false], ['history', false], ['ignoreeof', false], ['interactive-comments', true], ['keyword', false],
      ['monitor', false], ['noclobber', this.noclobber], ['noexec', false], ['noglob', this.noglob], ['nolog', false], ['notify', false],
      ['nounset', this.nounset], ['onecmd', false], ['physical', false], ['pipefail', this.pipefail], ['posix', false], ['privileged', false],
      ['verbose', false], ['vi', false], ['xtrace', this.xtrace]];
  }

  /** @internal set -o NAME / set +o NAME; false for an unknown name. */
  setOption(name: string, on: boolean): boolean {
    switch (name) {
      case 'errexit': this.errexit = on; return true;
      case 'nounset': this.nounset = on; return true;
      case 'pipefail': this.pipefail = on; return true;
      case 'xtrace': this.xtrace = on; return true;
      case 'noglob': this.noglob = on; return true;
      case 'noclobber': this.noclobber = on; return true;
      default: return this.optionList.some(o => o[0] === name); // accepted, no effect here
    }
  }

  /** @internal `read -u N`: the input opened on fd N by `exec N<file`, or undefined. */
  inputFd(fd: number): StdinBuffer | undefined { const f = this.fds.get(fd); return f instanceof StdinBuffer ? f : undefined; }

  /** @internal True for a built-in command (not one the app registered or replaced). */
  isBuiltin(name: string): boolean { return this.builtinNames?.has(name) === true; }

  /** @internal `local NAME[=VALUE]`: remembers the outer value and attributes, restored when the function returns. */
  declareLocal(name: string, value: string | null) {
    if (this.locals.length === 0) throw new ShellError('can only be used in a function');
    const top = this.locals[this.locals.length - 1];
    if (!top.has(name)) { top.set(name, { value: this.environment.get(name), attrs: this.varAttrs.get(name) }); this.varAttrs.delete(name); }
    this.markShellVariable(name);
    this.environment.set(name, value ?? '');
  }

  // ---------- variables ----------

  /** @internal */
  hasAttr(name: string, attr: string): boolean { return this.varAttrs.size > 0 && (this.varAttrs.get(name)?.includes(attr) ?? false); }
  /** @internal Whether a child bash sees the variable: declared -x, or put in `environment` by the host. */
  isExported(name: string): boolean { return this.hasAttr(name, 'x') || (this.environment.has(name) && !this.unexported.has(name)); }
  /** @internal Records that the shell (not the host) is about to create name, so it is not exported until `export`. */
  markShellVariable(name: string) { if (!this.environment.has(name)) this.unexported.add(name); }
  /** @internal */
  unexport(name: string) { this.setAttrs(name, '', 'x'); this.unexported.add(name); }

  /** @internal */
  setAttrs(name: string, on: string, off = '') {
    let f = this.varAttrs.get(name) ?? '';
    if (on.includes('l')) f = f.replace('u', '');
    if (on.includes('u')) f = f.replace('l', '');
    for (const c of on) if (!f.includes(c)) f += c;
    for (const c of off) f = f.replace(c, '');
    if (f.length === 0) this.varAttrs.delete(name); else this.varAttrs.set(name, f);
  }

  /** @internal Follows namerefs (declare -n ref=name) to the variable they stand for. */
  resolve(name: string): string {
    for (let i = 0; i < 10 && this.hasAttr(name, 'n'); i++) {
      const target = this.environment.get(name);
      if (!target) break;
      name = target;
    }
    return name;
  }

  /**
   * @internal Assigns a variable the way bash does: through namerefs, refusing readonly ones, and applying -i
   * (arithmetic), -l and -u. name[sub] sets an element; a plain assignment to an array sets element 0.
   */
  setVar(name: string, value: string, append = false) {
    name = this.resolve(name);
    const r = arrayRef(name);
    if (r && !r.keys && r.tail === '' && r.sub !== '@' && r.sub !== '*') {
      this.checkWritable(r.name);
      this.setElement(r.name, r.sub, this.converted(this.resolve(r.name), value, append ? this.getElement(r.name, r.sub) : null));
      return;
    }
    if (!isName(name)) throw new ShellError(`\`${name}': not a valid identifier`);
    this.checkWritable(name);
    if (name === 'SECONDS' && /^-?\d+$/.test(value)) { this.secondsBase = Date.now() - Number(value) * 1000; return; }
    const isArray = this.arrays.has(name);
    value = this.converted(name, value, !append ? null : isArray ? this.getElement(name, '0') : this.environment.get(name) ?? null);
    if (isArray) this.setElement(name, '0', value);
    else {
      this.chargeVariable(name, value.length, this.environment.get(name)?.length ?? 0);
      this.markShellVariable(name);
      this.environment.set(name, value);
      if (name === 'IFS') this.ifsUnset = false;
    }
  }

  private converted(name: string, value: string, old: string | null): string {
    const f = this.varAttrs.size ? this.varAttrs.get(name) : undefined;
    if (!f) return (old ?? '') + value;
    if (f.includes('i')) return (evalArith(this, value) + (old ? evalArith(this, old) : 0n)).toString();
    value = (old ?? '') + value;
    return f.includes('l') ? value.toLowerCase() : f.includes('u') ? value.toUpperCase() : value;
  }

  private checkWritable(name: string) {
    if (this.hasAttr(name, 'r')) throw new ShellError(`${name}: readonly variable`);
  }

  /** @internal The array called name (through namerefs), including FUNCNAME, BASH_SOURCE, PIPESTATUS and BASH_VERSINFO. */
  getArray(name: string): ShellArray | undefined {
    name = this.resolve(name);
    const a = this.arrays.get(name);
    if (a) return a;
    let items: string[] | null = null;
    if (name === 'FUNCNAME' && this.funcNames.length) items = [...this.funcNames].reverse();
    else if (name === 'BASH_SOURCE' && this.sources.length) items = [...this.sources].reverse();
    else if (name === 'PIPESTATUS') items = this.pipeStatus.map(String);
    else if (name === 'BASH_VERSINFO') items = BASH_VERSINFO;
    if (!items) return undefined;
    const built = new ShellArray(false);
    items.forEach((v, i) => built.set(String(i), v));
    return built;
  }

  /** Shell variables bash sets itself; they are not in the environment `env` prints. */
  private shellVariable(name: string): string | undefined {
    switch (name) {
      case 'IFS': return this.ifsUnset ? undefined : ' \t\n';
      case 'OPTIND': return '1';
      case 'BASH': return '/usr/bin/bash';
      case 'BASH_VERSION': return '5.2.21(1)-release';
      case 'UID': case 'EUID': return this.options.user === 'root' ? '0' : '1000';
      case 'PPID': return '4241';
      case 'BASHPID': return '4242';
      case 'SHLVL': return '1';
      case 'OSTYPE': return 'linux-gnu';
      case 'MACHTYPE': return 'x86_64-pc-linux-gnu';
      case 'HOSTTYPE': return 'x86_64';
      case 'LINENO': return String(this.lineno);
      case 'SECONDS': return String(Math.floor((Date.now() - this.secondsBase) / 1000));
      case 'EPOCHSECONDS': return String(Math.floor(Date.now() / 1000));
      case 'EPOCHREALTIME': return (Date.now() / 1000).toFixed(6);
    }
    return undefined;
  }

  /** $-: the single-letter options that are on. */
  private get optionFlags() {
    return (this.errexit ? 'e' : '') + (this.noglob ? 'f' : '') + 'h' + (this.nounset ? 'u' : '') + (this.xtrace ? 'x' : '') + 'B' + (this.noclobber ? 'C' : '') + 'c';
  }

  /** @internal The array to change: created if missing, copied first if a saved state still shares it. */
  writable(name: string, assoc = false): ShellArray {
    name = this.resolve(name);
    this.checkWritable(name);
    let a = this.arrays.get(name);
    if (!a) {
      a = new ShellArray(assoc);
      a.epoch = this.epoch;
      const scalar = this.environment.get(name);
      if (scalar !== undefined) { this.environment.delete(name); a.set('0', scalar); } // a plain variable becomes element 0
      this.arrays.set(name, a);
      return a;
    }
    if (a.epoch === this.epoch) return a;
    const c = a.clone(this.epoch);
    this.arrays.set(name, c);
    return c;
  }

  /** @internal `local arr` / `declare -A m`: a fresh array, restored to the caller's when the function returns. */
  declareArray(name: string, assoc: boolean, local: boolean) {
    this.checkWritable(name);
    if (local && this.localArrays.length > 0) {
      const top = this.localArrays[this.localArrays.length - 1];
      if (!top.has(name)) top.set(name, this.arrays.get(name));
    } else {
      const old = this.arrays.get(name);
      if (old && old.assoc === assoc) return;
    }
    if (local && this.locals.length > 0) this.declareLocal(name, null);
    this.environment.delete(name);
    const a = new ShellArray(assoc);
    a.epoch = this.epoch;
    this.arrays.set(name, a);
  }

  /** @internal The storage key for a subscript: the text itself for an associative array, else its arithmetic value. */
  arrayKey(array: ShellArray | undefined, subscript: string, name = ''): string {
    if (array?.assoc) return subscript;
    let i = evalArith(this, subscript);
    if (i < 0n) i += BigInt(array?.next ?? 1);
    if (i < 0n) throw new ShellError(`${name}: bad array subscript`);
    return i.toString();
  }

  /** @internal One element, or null if unset. A plain variable answers as element 0. */
  getElement(name: string, subscript: string): string | null {
    name = this.resolve(name);
    const a = this.getArray(name);
    if (a) return a.get(this.arrayKey(a, subscript, name)) ?? null;
    const scalar = this.environment.get(name);
    return scalar !== undefined && this.arrayKey(undefined, subscript, name) === '0' ? scalar : null;
  }

  /** @internal */
  setElement(name: string, subscript: string, value: string) {
    const a = this.writable(name);
    const key = this.arrayKey(a, subscript, name);
    const old = a.get(key);
    this.chargeVariable(name, key.length + value.length, old !== undefined ? key.length + old.length : 0);
    a.set(key, value);
  }

  /** @internal unset NAME or unset 'NAME[sub]'; `nameref` (-n) unsets a nameref itself, not what it names. */
  unset(target: string, nameref = false) {
    if (!nameref) target = this.resolve(target);
    const r = arrayRef(target);
    if (r && !r.keys && r.tail === '' && this.arrays.has(r.name) && r.sub !== '@' && r.sub !== '*') {
      const a = this.writable(r.name);
      a.delete(this.arrayKey(a, unquote(r.sub), r.name));
      return;
    }
    const name = r?.name ?? target;
    if (this.hasAttr(name, 'r')) throw new ShellError(`${name}: cannot unset: readonly variable`);
    this.environment.delete(name);
    this.arrays.delete(name);
    this.varAttrs.delete(name);
    this.unexported.delete(name);
    if (name === 'IFS') this.ifsUnset = true;
  }

  /** The elements ${name[@]...} stands for (keys for ${!name[@]}); null if expr is not a whole-array expansion. */
  private async arrayElements(expr: string, io: Io, quoted = false): Promise<string[] | null> {
    const r = arrayRef(expr);
    if (!r || (r.sub !== '@' && r.sub !== '*')) return null;
    const a = this.getArray(r.name);
    const name = this.resolve(r.name);
    let items: string[];
    if (!a) { const s = this.environment.get(name); items = s === undefined ? [] : [r.keys ? '0' : s]; }
    else items = r.keys ? a.keys() : a.values();
    if (r.keys || r.tail.length === 0) return items;
    const dop = defaultOp(r.tail);
    if (dop) {
      const [op, word] = dop;
      // ${a[@]:-word}: the array as a whole is tested; empty means no elements (or, with ':', nothing but empty text)
      const missing = items.length === 0 || (op[0] === ':' && items.join(r.sub === '*' ? this.ifsJoiner : ' ').length === 0);
      if (op.endsWith('?') && missing)
        throw this.fatal(io, `${r.name}[${r.sub}]: ${word.length > 0 ? await this.paramWordValue(word, quoted, io) : 'parameter null or not set'}`, 127);
      if (op.endsWith('=') && missing) throw new ShellError(`${r.name}[${r.sub}]: bad array subscript`);
      if (op.endsWith('-')) return missing ? [await this.paramWordValue(word, quoted, io)] : items;
      if (op.endsWith('+')) return missing ? [] : [await this.paramWordValue(word, quoted, io)];
      return items;
    }
    if (r.tail[0] === ':') {
      const spec = splitOnce(r.tail.slice(1), ':');
      let from = Number(await this.evalArithExpr(spec[0], io));
      const count = spec.length === 1 ? Infinity : Number(await this.evalArithExpr(spec[1], io));
      if (count < 0) throw new ShellError(`${spec[1].trim()}: substring expression < 0`);
      // an indexed array is sliced by index, so a sparse array skips its gaps
      if (a && !a.assoc) {
        const keys = a.keys().map(Number);
        if (from < 0) from += keys.length === 0 ? 0 : keys[keys.length - 1] + 1;
        if (from < 0) return [];
        const firstIdx = keys.findIndex(k => k >= from);
        return firstIdx < 0 ? [] : items.slice(firstIdx, count === Infinity ? undefined : firstIdx + count);
      }
      if (from < 0) from += items.length;
      if (from < 0 || from > items.length) return [];
      return items.slice(from, count === Infinity ? undefined : from + count);
    }
    // any other operator applies to each element: ${files[@]%.txt}, ${names[@]^^}
    const result: string[] = [];
    for (const item of items) result.push(await this.elementOp(item, r.tail, io, quoted));
    return result;
  }

  /** Applies a ${...} operator to one value by expanding it through a scratch variable. */
  private async elementOp(value: string | null, rest: string, io: Io, quoted = false): Promise<string> {
    if (rest.length === 0) return value ?? '';
    if (value === null) this.environment.delete(SCRATCH); else this.environment.set(SCRATCH, value);
    try { return await this.expandParameter(SCRATCH + rest, io, quoted); } finally { this.environment.delete(SCRATCH); }
  }

  /** @internal `unset 'a[${#a[@]}-1]'`: name[sub] with the subscript's expansions done. */
  async expandSubscript(target: string, io: Io): Promise<string> {
    const r = arrayRef(target);
    return r && !r.keys && r.tail === '' && /[$`]/.test(r.sub) ? `${r.name}[${await this.subscript(r.sub, io)}]` : target;
  }

  private async subscript(sub: string, io: Io): Promise<string> {
    return /[$`"']/.test(sub) ? this.expandSingle(parseQuoted(unquote(sub), false), io) : sub;
  }

  private async assignArray(a: Assignment, io: Io) {
    const name = this.resolve(a.name);
    if (a.elements === undefined) {
      const key = await this.subscript(a.index!, io);
      const value = await this.expandAssignment(a.value, io);
      if (this.xtrace) io.err.write(`+ ${name}[${key}]${a.append ? '+=' : '='}${traceQuote(value)}\n`);
      this.setVar(`${name}[${key}]`, value, a.append);
      return;
    }
    let target: ShellArray;
    if (a.append) target = this.writable(name);
    else {
      this.checkWritable(name);
      target = new ShellArray(this.arrays.get(name)?.assoc ?? false);
      target.epoch = this.epoch;
      this.environment.delete(name);
    }
    for (const w of parseWords(a.elements)) {
      // [key]=value sets that element; anything else is appended, after the usual splitting and globbing
      const kv = splitSubscript(w);
      if (kv) target.set(this.arrayKey(target, await this.expandSingle(kv[0], io)), await this.expandSingle(kv[1], io));
      else for (const field of await this.expandWords([w], io)) target.set(target.assoc ? field : String(target.next), field);
    }
    this.arrays.set(name, target);
  }

  /**
   * `local a=(x y)`, `declare -A m`, `declare -a list=()`: the array arguments are handled here, because a built-in
   * only receives expanded text. Returns the words left for the built-in, or null if none were array declarations.
   */
  private async declareArrays(words: Word[], io: Io): Promise<Word[] | null> {
    const flags = words.slice(1).map(literal).filter((t): t is string => t !== null && t.startsWith('-')).join('');
    if (/[pfF]/.test(flags)) return null;
    const first = literal(words[0]);
    const local = first === 'local' || (this.inFunction && !flags.includes('g'));
    const assoc = flags.includes('A');
    let any = false;
    const attrs = [...flags].filter(c => c === 'r' || c === 'x').join('') + (first === 'readonly' ? 'r' : '');
    const keep: Word[] = [words[0]];
    for (const w of words.slice(1)) {
      const l = literal(w);
      if (l?.startsWith('-')) continue;
      const assignment = w.assign;
      if (assignment?.elements !== undefined) {
        this.declareArray(assignment.name, assoc, local);
        await this.assignArray(assignment, io);
      } else if (!assignment && (assoc || flags.includes('a')) && l !== null && isName(l)) this.declareArray(l, assoc, local);
      else { keep.push(w); continue; }
      if (attrs.length > 0) this.setAttrs(assignment?.name ?? l!, attrs);
      any = true;
    }
    return any ? keep : null;
  }

  // ---------- expansion ----------

  /** A variable's value as text: positional and special parameters included; "" when unset. */
  getVariable(name: string): string {
    if (/^\d/.test(name)) {
      const n = Number(name);
      return n === 0 ? this.arg0 : n <= this.positional.length ? this.positional[n - 1] : '';
    }
    switch (name) {
      case '?': return String(this.lastExitCode);
      case '$': return '4242';
      case '!': return this.lastBg === 0 ? '' : String(this.lastBg);
      case '#': return String(this.positional.length);
      case '@': return this.positional.join(' ');
      case '*': return this.positional.join(this.ifsJoiner);
      case '-': return this.optionFlags;
      case '_': return this.lastArg;
      case 'PWD': return this.cwdPath;
      case 'RANDOM': return String(Math.floor(Math.random() * 32768));
    }
    return this.lookUp(name) ?? '';
  }

  /** A named variable's value (element 0 of an array), or undefined when it is unset. */
  private lookUp(name: string): string | undefined {
    name = this.resolve(name);
    const a = this.getArray(name);
    if (a) return a.get('0');
    return this.environment.get(name) ?? this.shellVariable(name);
  }

  /** @internal The IFS that splitting uses: the default when it is unset. */
  get ifs(): string { return this.environment.get('IFS') ?? ' \t\n'; }
  /** "$*" and "${a[*]}" join with the first character of IFS (a space when IFS is unset). */
  private get ifsJoiner(): string { const ifs = this.environment.get('IFS'); return ifs === undefined ? ' ' : ifs.slice(0, 1); }

  private isSet(name: string): boolean {
    if (['?', '$', '#', '@', '*', '-', '_', 'PWD', 'RANDOM', '0'].includes(name)) return true;
    if (/^\d+$/.test(name)) return Number(name) <= this.positional.length;
    if (name === '!') return this.lastBg !== 0;
    return this.lookUp(name) !== undefined;
  }

  private async expandPart(part: WordPart, io: Io): Promise<string> {
    switch (part.k) {
      case 'lit': case 'exp': return part.text;
      case 'var': return this.expandParameter(part.expr, io, part.q);
      case 'cmd': return this.capture(part.src, io);
      case 'arith': return (await this.arithExpansion(part.expr, io)).toString();
      case 'proc': return this.processSubstitution(part.src, io);
    }
  }

  /** @internal Expands $(cmd), `cmd` and ${...} inside an arithmetic expression, then evaluates it. */
  async evalArithExpr(expr: string, io: Io): Promise<bigint> {
    // each level of $(( $(( ... )) )) re-reads what is inside it, so unbounded nesting is quadratic work
    if (++this.arithDepth > 100) { this.arithDepth = 0; throw new LimitExceeded('arithmetic is nested too deeply'); }
    try { return evalArith(this, /[$`]/.test(expr) ? await this.expandSingle(parseQuoted(expr, false), io) : expr); } finally { if (this.arithDepth > 0) this.arithDepth--; }
  }

  /** $(( )): unlike (( )) and let, an error here ends the script, as in bash. */
  private async arithExpansion(expr: string, io: Io): Promise<bigint> {
    try { return await this.evalArithExpr(expr, io); } catch (e) {
      if (e instanceof ShellError) throw this.fatal(io, e.message, 1);
      throw e;
    }
  }

  /** An expansion error that ends a non-interactive bash: the shell exits (a subshell with status 1). */
  private fatal(io: Io, message: string, code: number): ExitSignal {
    io.err.write(`bash: ${message}\n`);
    return new ExitSignal(this.subshell > 0 ? 1 : code);
  }

  /** ${!name...}: the expression with name's value in its place; null if expr is not an indirection. */
  private indirectTarget(expr: string): string | null {
    if (expr.length < 2 || expr[0] !== '!' || PREFIX_RE.test(expr) || arrayRef(expr)?.keys) return null;
    const m = INDIRECT_RE.exec(expr);
    if (!m) return null;
    const name = m[1];
    if (this.hasAttr(name, 'n')) return null; // ${!ref} of a nameref is the name it refers to
    const target = this.getVariable(name);
    if (target.length === 0) throw new ShellError(`${name}: invalid indirect expansion`);
    return target + m[2];
  }

  /** The word of ${x op word}, lexed once per distinct text. */
  private paramWord(text: string, quoted: boolean, pattern = false): Word {
    const key = (quoted ? '1' : '0') + (pattern ? 'p' : '') + text;
    let w = this.paramWords.get(key);
    if (w) return w;
    if (this.paramWords.size >= 512) this.paramWords.clear();
    try { w = parseParamWord(text, quoted, pattern); } catch (e) {
      if (e instanceof ParseError) throw new ShellError(e.message);
      throw e;
    }
    this.paramWords.set(key, w);
    return w;
  }

  private paramWordValue(text: string, quoted: boolean, io: Io) { return this.expandSingle(this.paramWord(text, quoted), io); }

  /** ${x/pat/rep}'s replacement: literal pieces and, for an unquoted &, the match (bash 5.2). */
  private async replacement(text: string, quoted: boolean, io: Io): Promise<(string | null)[]> {
    const out: (string | null)[] = [];
    const parts = this.paramWord(text, quoted, true).parts;
    for (let i = 0; i < parts.length; i++) {
      const part = parts[i];
      let t = await this.expandPart(part, io);
      if (i === 0 && part.k === 'lit' && !part.q) { const tp = this.tildePrefix(t); if (tp) t = tp[0] + t.slice(tp[1]); }
      if (part.q || part.k !== 'lit') { out.push(t); continue; }
      t.split('&').forEach((piece, k) => { if (k > 0) out.push(null); out.push(piece); });
    }
    return out;
  }

  /** ${name}, ${#name}, ${name:-x}, ${name%pat}, ${name/a/b}, ${name:1:2}, ${name^^}, ${name@Q}, ${!name} ... */
  private async expandParameter(expr: string, io: Io, quoted = false): Promise<string> {
    if (expr.length > 1 && expr[0] === '!') {
      const pm = PREFIX_RE.exec(expr);
      if (pm) return [...new Set([...this.environment.keys(), ...this.arrays.keys()])].filter(k => k.startsWith(pm[1]) && k !== SCRATCH).sort(ordinal).join(' ');
      if (isName(expr.slice(1)) && this.hasAttr(expr.slice(1), 'n')) return this.environment.get(expr.slice(1)) ?? '';
      const target = this.indirectTarget(expr);
      if (target !== null) return this.expandParameter(target, io, quoted);
    }
    if (expr.length > 1 && expr[0] === '#') {
      const counted = arrayRef(expr.slice(1));
      if (counted && !counted.keys && counted.tail === '') {
        if (counted.sub === '@' || counted.sub === '*') {
          const a = this.getArray(counted.name);
          return String(a ? a.size : this.lookUp(counted.name) !== undefined ? 1 : 0);
        }
        return String([...(this.getElement(counted.name, await this.subscript(counted.sub, io)) ?? '')].length);
      }
    }
    // as one string, ${a[*]} joins with the first character of IFS and ${a[@]} with a space
    const all = await this.arrayElements(expr, io, quoted);
    if (all) return all.join(arrayRef(expr)!.sub === '*' ? this.ifsJoiner : ' ');
    const one = arrayRef(expr);
    if (one && !one.keys) {
      let element: string | null;
      try { element = this.getElement(one.name, await this.subscript(one.sub, io)); } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        io.err.write(`bash: ${e.message}\n`); // like bash: reported, and the expansion is empty
        element = null;
      }
      if (element === null && this.nounset && !defaultOp(one.tail)) throw this.fatal(io, `${one.name}[${one.sub}]: unbound variable`, 127);
      return this.elementOp(element, one.tail, io, quoted);
    }
    if (expr.length > 1 && expr[0] === '#') {
      const n = expr.slice(1);
      if (n === '@' || n === '*') return String(this.positional.length);
      if (PARAM_RE.test(n) && PARAM_RE.exec(n)![2] === '') {
        this.checkSet(n, io);
        return String([...this.getVariable(n)].length);
      }
    }
    const m = PARAM_RE.exec(expr);
    if (!m) throw new ShellError(`\${${expr}}: bad substitution`);
    const name = m[1], rest = m[2];
    const value = this.getVariable(name);
    const dop = defaultOp(rest);
    if (!dop) this.checkSet(name, io); // defaults handle unset variables
    if (rest.length === 0) return value;
    if (dop) {
      const [op, word] = dop;
      const missing = op[0] === ':' ? value.length === 0 : !this.isSet(name);
      switch (op[op.length - 1]) {
        case '-': return missing ? this.paramWordValue(word, quoted, io) : value;
        case '=':
          if (!missing) return value;
          if (!isName(name)) throw new ShellError(`$${name}: cannot assign in this way`);
          this.setVar(name, await this.paramWordValue(word, quoted, io));
          return this.getVariable(name);
        case '+': return missing ? '' : this.paramWordValue(word, quoted, io);
        default:
          if (missing) throw this.fatal(io, `${name}: ${word.length > 0 ? await this.paramWordValue(word, quoted, io) : op[0] === ':' ? 'parameter null or not set' : 'parameter not set'}`, 127);
          return value;
      }
    }
    if (rest[0] === '#' || rest[0] === '%') {
      const longest = rest.length > 1 && rest[1] === rest[0];
      const re = globRegex(await this.pattern(this.paramWord(rest.slice(longest ? 2 : 1), quoted, true), io), false, true);
      const n = value.length;
      if (rest[0] === '#') {
        for (let i = longest ? n : 0; longest ? i >= 0 : i <= n; i += longest ? -1 : 1) if (re.test(value.slice(0, i))) return value.slice(i);
      } else {
        for (let i = longest ? 0 : n; longest ? i <= n : i >= 0; i += longest ? 1 : -1) if (re.test(value.slice(i))) return value.slice(0, i);
      }
      return value;
    }
    if (rest[0] === '/') {
      let body = rest.slice(1);
      const mode = body.length > 0 && '/#%'.includes(body[0]) ? body[0] : ' ';
      if (mode !== ' ') body = body.slice(1);
      // the pattern ends at the first unquoted '/'; after `//` a leading '/' is part of the pattern
      let slash = -1;
      for (let i = mode === '/' && body.startsWith('/') ? 1 : 0; i < body.length; i++) {
        if (body[i] === '\\') { i++; continue; }
        if (body[i] === "'" && !quoted) { const q = body.indexOf("'", i + 1); i = q >= 0 ? q : body.length; continue; }
        if (body[i] === '"') { for (i++; i < body.length && body[i] !== '"'; i++) if (body[i] === '\\') i++; continue; }
        if (body[i] === '/') { slash = i; break; }
      }
      const pat = globBody(await this.pattern(this.paramWord(slash < 0 ? body : body.slice(0, slash), quoted, true), io), true);
      const rep = slash < 0 ? [] : await this.replacement(body.slice(slash + 1), quoted, io);
      const plain = rep.every(p => p !== null) ? rep.join('') : null;
      // an empty pattern matches nothing, except at an anchor: ${x/#/pre-} and ${x/%/-post} add to the ends
      if (pat.length === 0) return mode === '#' ? (plain ?? '') + value : mode === '%' ? value + (plain ?? '') : value;
      const re = cachedRegex(mode === '#' ? '^(?:' + pat + ')' : mode === '%' ? '(?:' + pat + ')$' : pat, mode === '/' ? 'gs' : 's');
      const repLen = rep.reduce((n, p) => n + (p?.length ?? 0), 0);
      // ${x//?/$x} grows with the square of x: refuse before building it
      if (mode === '/' && value.length + repLen * (value.length + 1) > this.options.maxStringBytes) {
        const count = value.match(re)?.length ?? 0;
        if (value.length + repLen * count > this.options.maxStringBytes) throw tooLarge('expansion', this.options.maxStringBytes, 'write the data to a file instead');
      }
      re.lastIndex = 0;
      return value.replace(re, (match: string) => (plain !== null ? plain : rep.map(p => p ?? match).join('')));
    }
    if (rest[0] === '^' || rest[0] === ',' || rest[0] === '~') {
      // ${x^} ${x^^} ${x,} ${x,,} ${x~} ${x~~}, optionally only for characters matching a pattern: ${x^^[aeiou]}
      const every = rest.length > 1 && rest[1] === rest[0];
      const patText = rest.slice(every ? 2 : 1);
      const only = patText.length > 0 ? globRegex(await this.pattern(this.paramWord(patText, quoted), io), false, true) : null;
      const chars = [...value];
      for (let i = 0; i < chars.length && (every || i === 0); i++) {
        const c = chars[i];
        if (only && !only.test(c)) continue;
        const lower = c !== c.toUpperCase();
        chars[i] = rest[0] === '^' || (rest[0] === '~' && lower) ? c.toUpperCase() : c.toLowerCase();
      }
      return chars.join('');
    }
    if (rest.length === 2 && rest[0] === '@') {
      if (rest[1] === 'a') return attributeFlags(this, this.resolve(name));
      if (!this.isSet(name)) return '';
      switch (rest[1]) {
        case 'Q': case 'K': return quoteValue(value);
        case 'E': return unescape(value, true);
        case 'U': return value.toUpperCase();
        case 'L': return value.toLowerCase();
        case 'u': return value.length === 0 ? value : value[0].toUpperCase() + value.slice(1);
        case 'A': {
          const flags = attributeFlags(this, this.resolve(name));
          return flags ? `declare -${flags} ${name}=${quoteValue(value)}` : `${name}=${quoteValue(value)}`;
        }
        case 'P': return value;
      }
      throw new ShellError(`\${${expr}}: bad substitution`);
    }
    if (rest[0] === ':') {
      const spec = splitOnce(rest.slice(1), ':');
      const chars = [...value];
      let offset = Number(await this.evalArithExpr(spec[0], io));
      if (offset < 0) offset += chars.length;
      if (offset < 0 || offset > chars.length) return '';
      const tail = chars.slice(offset);
      if (spec.length === 1) return tail.join('');
      let len = Number(await this.evalArithExpr(spec[1], io));
      if (len < 0 && (len += tail.length) < 0) throw new ShellError(`${spec[1].trim()}: substring expression < 0`);
      return tail.slice(0, len).join('');
    }
    throw new ShellError(`\${${expr}}: bad substitution`);
  }

  /** set -u: expanding an unset variable is an error that ends the (sub)shell, like bash. */
  private checkSet(name: string, io: Io) {
    if (!this.nounset || name === '@' || name === '*' || this.isSet(name)) return;
    throw this.fatal(io, `${name}: unbound variable`, 127);
  }

  /** $(...) and <(...) run in a subshell that, as in bash, does not inherit set -e (unless shopt inherit_errexit). */
  private async runSubstitution(source: string, io: Io): Promise<number> {
    const errexit = this.errexit;
    if (!this.shopts.has('inherit_errexit')) this.errexit = false;
    try { return await this.runScriptText(source, io, true); } finally { this.errexit = errexit; }
  }

  /** <(cmd): writes cmd's output to /dev/fd/N (removed after the tool call) and returns that path. */
  private async processSubstitution(source: string, io: Io): Promise<string> {
    const output = new BufferWriter(this.options.maxStringBytes);
    await this.runSubstitution(source, { ...io, stdin: new StdinBuffer(''), out: output });
    const path = `/dev/fd/${63 + this.procSubCount++}`;
    this.fs.writeText(path, output.toString());
    return path;
  }

  private async capture(source: string, io: Io): Promise<string> {
    const fileWord = readFileWord(source);
    if (fileWord) { // $(< file) is the file's contents
      const target = await this.expandSingle(fileWord, io);
      try {
        const text = this.agentFs.readText(this.resolvePath(target));
        this.lastExitCode = 0;
        return text.replace(/\n+$/, '');
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        io.err.write(`bash: ${target}: ${e.message.slice(e.message.lastIndexOf(': ') + 2)}\n`);
        this.lastExitCode = 1;
        return '';
      }
    }
    const output = new BufferWriter(this.options.maxStringBytes);
    this.lastExitCode = await this.runSubstitution(source, { ...io, stdin: new StdinBuffer(''), out: output });
    return output.toString().replace(/\n+$/, '');
  }

  /** @internal Expands a word without field splitting or globbing (redirect targets, heredocs, case subjects). */
  async expandSingle(word: Word, io: Io): Promise<string> {
    let out = '';
    for (let i = 0; i < word.parts.length; i++) {
      const part = word.parts[i];
      let text = await this.expandPart(part, io);
      if (i === 0 && part.k === 'lit' && !part.q) { const tp = this.tildePrefix(text); if (tp) text = tp[0] + text.slice(tp[1]); }
      if (out.length + text.length > this.options.maxStringBytes) throw tooLarge('expansion', this.options.maxStringBytes, 'write the data to a file instead');
      out += text;
    }
    return out;
  }

  /** @internal An assignment's value: one string, with ~ expanded at the start and after each ':' (PATH=~/bin:~/x). */
  async expandAssignment(word: Word, io: Io): Promise<string> {
    let out = '';
    for (let i = 0; i < word.parts.length; i++) {
      const part = word.parts[i];
      let text = await this.expandPart(part, io);
      if (part.k === 'lit' && !part.q && text.includes('~')) {
        const atStart = i === 0 || out.endsWith(':');
        text = text.replace(/(?<=^|:)~([A-Za-z0-9_.+-]*)(?=\/|:|$)/g, (m0, user: string, index: number) =>
          (index === 0 && !atStart ? null : this.tildeValue(user)) ?? m0);
      }
      out += text;
    }
    return out;
  }

  /** @internal Expands a word into a glob pattern: quoted text is escaped, unquoted text keeps its wildcards. */
  async pattern(word: Word, io: Io): Promise<string> {
    let out = '';
    for (const part of word.parts) {
      const text = await this.expandPart(part, io);
      out += part.q ? escapeGlob(text) : text;
    }
    return out;
  }

  /** ~, ~user, ~+ and ~-: the directory they name, or null for an unknown user. */
  private tildeValue(user: string): string | null {
    if (user === '') return this.environment.get('HOME') ?? '/';
    if (user === '+') return this.cwdPath;
    if (user === '-') return this.environment.get('OLDPWD') ?? null;
    if (user === 'root') return '/root';
    if (user === this.options.user) return '/home/' + user;
    return null;
  }

  /** A leading ~ or ~user (up to the first '/'): its directory and how many characters it spans. */
  private tildePrefix(text: string): [string, number] | null {
    if (!text.startsWith('~')) return null;
    let end = text.indexOf('/');
    if (end < 0) end = text.length;
    const home = this.tildeValue(text.slice(1, end));
    return home === null ? null : [home, end];
  }

  /**
   * An unquoted ${p:-word} or ${p:+word} whose word applies: the word's parts, so quotes inside the word still keep
   * it from being split (${v:-"a b"} is one field). Null when p's own value applies.
   */
  private async alternativeWord(expr: string, io: Io): Promise<WordPart[] | null> {
    const element = arrayRef(expr);
    const match = element ? null : PARAM_RE.exec(expr);
    const rest = element && !element.keys && element.sub !== '@' && element.sub !== '*' ? element.tail : match ? match[2] : '';
    const dop = defaultOp(rest);
    if (!dop || (dop[0].slice(-1) !== '-' && dop[0].slice(-1) !== '+')) return null;
    const [op, word] = dop;
    const value = element ? this.getElement(element.name, await this.subscript(element.sub, io))
      : this.isSet(match![1]) ? this.getVariable(match![1]) : null;
    const missing = op[0] === ':' ? !value : value === null;
    if (op.endsWith('-') ? !missing : missing) return op.endsWith('-') ? null : [];
    // the word's unquoted text is split like any expansion's
    return this.paramWord(word, false).parts.map(p => (p.k === 'lit' && !p.q ? { k: 'exp', text: p.text, q: false } : p));
  }

  /** @internal Expands words into fields: brace expansion, parameters, splitting on IFS, globbing. */
  async expandWords(words: Word[], io: Io): Promise<string[]> {
    const result: string[] = [];
    let total = 0; // all the words together, so {1..10000}$big can't multiply a big value
    const max = this.options.maxStringBytes;
    for (const word of words.flatMap(braceExpand)) {
      // fast path: one unquoted literal without wildcards or tilde
      if (word.parts.length === 1 && word.parts[0].k === 'lit') {
        const p = word.parts[0];
        if (p.q || (!hasWildcard(p.text) && !p.text.startsWith('~'))) { result.push(p.text); continue; }
      }
      interface Field { text: string; pattern: string; glob: boolean; content: boolean }
      const fields: Field[] = [];
      let cur: Field = { text: '', pattern: '', glob: false, content: false };
      const end = () => { if (cur.content) fields.push(cur); cur = { text: '', pattern: '', glob: false, content: false }; };
      const addText = (s: string, quoted: boolean) => {
        if ((total += s.length) > max) throw tooLarge('expansion', max, 'write the data to a file instead');
        cur.text += s;
        cur.pattern += quoted ? escapeGlob(s) : s;
        if (!quoted && hasWildcard(s)) cur.glob = true;
        cur.content = true;
      };
      // unquoted $VAR / $(cmd) / $((...)): split into fields on $IFS
      const addSplit = (text: string) => {
        const ifs = this.ifs;
        const pieces = splitFields(text, ifs);
        if (pieces.length === 0) { if (text.length > 0 && ifs.length > 0) end(); return; }
        if (ifs.includes(text[0])) end();
        pieces.forEach((piece, k) => { if (k > 0) end(); addText(piece, false); });
        if (ifs.includes(text[text.length - 1])) end();
      };
      // "$@" / "${a[@]}": one field per item, and none at all when there are none; unquoted, each item is split
      const addEach = (items: string[], quoted: boolean) => {
        items.forEach((item, k) => { if (k > 0) end(); if (quoted) addText(item, true); else addSplit(item); });
      };
      let parts: WordPart[] = word.parts;
      for (let i = 0; i < parts.length; i++) {
        let part = parts[i];
        if (part.k === 'var') {
          const name = this.indirectTarget(part.expr) ?? part.expr;
          if (name === '@' || (name === '*' && !part.q)) { addEach(this.positional, part.q); continue; }
          if ((name[0] === '@' || name[0] === '*') && name[1] === ':' && !defaultOp(name.slice(1))) {
            // ${@:2} and ${@:2:3} slice the parameters, not the characters; ${@:0} starts at $0
            const spec = splitOnce(name.slice(2), ':');
            const allParams = [this.arg0, ...this.positional];
            let from = Number(await this.evalArithExpr(spec[0], io));
            if (from < 0) from += allParams.length;
            const count = spec.length === 1 ? allParams.length : Number(await this.evalArithExpr(spec[1], io));
            const items = from < 0 || from > allParams.length ? [] : allParams.slice(from, from + Math.max(0, Math.min(count, allParams.length)));
            if (part.q && name[0] === '*') addText(items.join(this.ifsJoiner), true); else addEach(items, part.q);
            continue;
          }
          const ar = arrayRef(name);
          if (ar && (ar.sub === '@' || ar.sub === '*') && (ar.sub === '@' || !part.q)) {
            const elements = await this.arrayElements(name, io, part.q);
            if (elements) { addEach(elements, part.q); continue; }
          }
          if (!part.q && /[-+]/.test(name)) {
            const alternative = await this.alternativeWord(name, io);
            if (alternative) {
              parts = [...parts.slice(0, i), ...alternative, ...parts.slice(i + 1)];
              i--;
              continue;
            }
          }
          if (name !== part.expr) part = { ...part, expr: name };
        }
        let text = await this.expandPart(part, io);
        if (part.k === 'lit') {
          if (i === 0 && !part.q) {
            const tp = this.tildePrefix(text);
            if (tp) {
              addText(tp[0], true); // the home path is never globbed
              text = text.slice(tp[1]);
              if (text.length === 0) continue;
            }
          }
          addText(text, part.q);
          continue;
        }
        if (part.q) addText(text, true);
        else addSplit(text);
      }
      end();
      for (const f of fields) {
        const globbing = f.glob && !this.noglob;
        const matches = globbing ? expandGlob({
          fs: this.agentFs, cwd: this.cwdPath, dotglob: this.shopts.has('dotglob'), globstar: this.shopts.has('globstar'), nocaseglob: this.shopts.has('nocaseglob'),
        }, f.pattern) : [];
        if (matches.length > 0) result.push(...matches);
        else if (!(globbing && this.shopts.has('nullglob'))) result.push(f.text);
      }
    }
    return result;
  }
}

/** Splits text on IFS like bash: whitespace runs collapse and are trimmed; other IFS characters delimit one field each.
 * maxFields > 0 leaves the rest of the line in the last field (`read a b`). */
export function splitFields(text: string, ifs: string, maxFields = 0): string[] {
  if (ifs.length === 0) return text.length > 0 ? [text] : [];
  const ws = [...' \t\n'].filter(c => ifs.includes(c)).join('');
  const other = [...ifs].filter(c => !ws.includes(c)).join('');
  if (ws.length > 0) {
    let s = 0, e = text.length;
    while (s < e && ws.includes(text[s])) s++;
    while (e > s && ws.includes(text[e - 1])) e--;
    text = text.slice(s, e);
  }
  if (text.length === 0) return [];
  const set = (chars: string) => '[' + chars.replace(/[\\\]^[-]/g, '\\$&') + ']';
  const wsRx = set(ws), otherRx = set(other);
  const sep = other.length === 0 ? wsRx + '+' : ws.length === 0 ? otherRx : `${wsRx}*${otherRx}${wsRx}*|${wsRx}+`;
  const re = cachedRegex(sep, 'g');
  const fields: string[] = [];
  let last = 0;
  re.lastIndex = 0;
  for (let m = re.exec(text); m; m = re.exec(text)) {
    if (maxFields > 0 && fields.length === maxFields - 1) break;
    fields.push(text.slice(last, m.index));
    last = m.index + m[0].length;
  }
  fields.push(text.slice(last));
  if (maxFields <= 0 && other.length > 0 && fields.length > 0 && fields[fields.length - 1] === '') fields.pop(); // "a,b," is two fields
  return fields;
}

function splitOnce(s: string, sep: string): string[] {
  const i = s.indexOf(sep);
  return i < 0 ? [s] : [s.slice(0, i), s.slice(i + 1)];
}

/** [key]=value in an array literal: the key and value words, or null. */
function splitSubscript(w: Word): [Word, Word] | null {
  const first = w.parts[0];
  if (!first || first.k !== 'lit' || first.q || !first.text.startsWith('[')) return null;
  // find the closing ]= in the literal text of the parts
  let depth = 0;
  for (let pi = 0; pi < w.parts.length; pi++) {
    const p = w.parts[pi];
    if (p.k !== 'lit' || p.q) continue;
    for (let i = pi === 0 ? 0 : 0; i < p.text.length; i++) {
      const c = p.text[i];
      if (c === '[') depth++;
      else if (c === ']' && --depth === 0) {
        if (p.text[i + 1] !== '=') return null;
        const keyParts: WordPart[] = [...w.parts.slice(0, pi), { k: 'lit', text: p.text.slice(0, i), q: false }];
        keyParts[0] = { k: 'lit', text: (keyParts[0] as { text: string }).text.slice(1), q: false };
        if (pi === 0) keyParts.splice(0, keyParts.length, { k: 'lit', text: p.text.slice(1, i), q: false });
        const valueParts: WordPart[] = [{ k: 'lit', text: p.text.slice(i + 2), q: false }, ...w.parts.slice(pi + 1)];
        return [{ parts: keyParts }, { parts: valueParts }];
      }
    }
  }
  return null;
}

/** The file word of `$(< file)`, or null when the substitution is anything else. */
function readFileWord(source: string): Word | null {
  if (!source.includes('<')) return null;
  let script: Script;
  try { script = parse(source); } catch { return null; }
  if (script.items.length !== 1) return null;
  const item = script.items[0];
  if (item.rest.length || item.first.negate || item.first.commands.length !== 1) return null;
  const cmd = item.first.commands[0];
  if (cmd.k !== 'simple' || cmd.words.length || cmd.assigns.length || cmd.redirects.length !== 1 || cmd.redirects[0].op !== '<') return null;
  return cmd.redirects[0].target ?? null;
}

function withFd(io: Io, fd: number, target: Writer | StdinBuffer): Io {
  if (fd === 0) return target instanceof StdinBuffer ? { ...io, stdin: target } : io;
  if (fd === 1) return target instanceof StdinBuffer ? io : { ...io, out: target };
  if (fd === 2) return target instanceof StdinBuffer ? io : { ...io, err: target };
  return { ...io, fds: new Map(io.fds ?? []).set(fd, target) };
}

/** set -x: an argument as bash traces it, quoted only when it needs to be. */
function traceQuote(s: string): string { return /^[A-Za-z0-9_./=:,+@%^-]+$/.test(s) ? s : quoteValue(s); }

function stopped(why: string) {
  return `[stopped: ${why}. Running it again will hit the same limit. Files and variables changed before this point are kept.]`;
}
function seconds(ms: number) { return `${Number((ms / 1000).toFixed(3))} s`; }

const VERSIONS: Record<string, string> = {
  grep: 'grep (GNU grep) 3.11', sed: 'sed (GNU sed) 4.9', find: 'find (GNU findutils) 4.9.0', xargs: 'xargs (GNU findutils) 4.9.0',
  diff: 'diff (GNU diffutils) 3.10', jq: 'jq-1.7.1', tree: 'tree v2.1.1', bash: 'GNU bash, version 5.2.21(1)-release (x86_64-pc-linux-gnu)',
  sh: 'GNU bash, version 5.2.21(1)-release (x86_64-pc-linux-gnu)', file: 'file-5.45', sudo: 'Sudo version 1.9.15p5',
  less: 'less 590 (GNU regular expressions)', ps: 'ps from procps-ng 4.0.4', free: 'free from procps-ng 4.0.4', uptime: 'uptime from procps-ng 4.0.4',
  column: 'column from util-linux 2.39.3', rev: 'rev from util-linux 2.39.3', more: 'more from util-linux 2.39.3', egrep: 'grep (GNU grep) 3.11',
  fgrep: 'grep (GNU grep) 3.11', tar: 'tar (GNU tar) 1.35', gzip: 'gzip 1.12', gunzip: 'gzip 1.12', zcat: 'gzip 1.12',
};
const NO_STANDARD_OPTIONS = new Set(['sudo', 'su', 'echo', 'printf', 'test', '[', 'true', 'false', ':', 'awk', 'help', 'exit', 'return', 'break',
  'continue', 'set', 'eval', 'command', 'type', 'alias', 'export', 'local', 'declare', 'read', 'cd']);

/** `tool --version` / `tool --help` for built-ins, so probing them looks like a real system. */
function standardOption(ctx: CommandContext, cmd: RegisteredCommand, arg: string): boolean {
  if ((arg !== '--version' && arg !== '--help') || NO_STANDARD_OPTIONS.has(cmd.name)) return false;
  if (arg === '--version') ctx.stdout.write((VERSIONS[cmd.name] ?? `${cmd.name} (GNU coreutils) 9.4`) + '\n');
  else ctx.stdout.write(cmd.usage ?? `Usage: ${cmd.name} [OPTION]... [ARG]...\n${cmd.description}.\n`);
  return true;
}

/** Evaluates [[ ... ]]. */
class CondEvaluator {
  private static readonly UNARY = new Set(['-z', '-n', '-e', '-f', '-d', '-s', '-r', '-w', '-x', '-L', '-h', '-a', '-v', '-o', '-t', '-b', '-c', '-p', '-S', '-g', '-u', '-k', '-O', '-G', '-N']);
  private static readonly BINARY = new Set(['==', '=', '!=', '=~', '<', '>', '-eq', '-ne', '-lt', '-le', '-gt', '-ge', '-nt', '-ot', '-ef']);
  private p = 0;
  constructor(private sh: Shell, private items: Word[], private io: Io) {}

  private isOp(s: string) { return this.p < this.items.length && literal(this.items[this.p]) === s; }
  private isBinary(w: Word) { const l = literal(w); return l !== null && CondEvaluator.BINARY.has(l); }

  async evaluate(): Promise<boolean> {
    if (this.items.length === 0) throw new ShellError('syntax error in conditional expression', 2);
    const v = await this.or();
    if (this.p < this.items.length) throw new ShellError(`syntax error in conditional expression near \`${literal(this.items[this.p]) ?? ''}'`, 2);
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
      if (!this.isOp(')')) throw new ShellError("syntax error in conditional expression: expected `)'", 2);
      this.p++;
      return v;
    }
    if (this.p >= items.length) throw new ShellError('syntax error in conditional expression: unexpected end', 2);
    const u = literal(items[this.p]);
    if (u !== null && CondEvaluator.UNARY.has(u) && this.p + 1 < items.length && !(this.p + 2 < items.length && this.isBinary(items[this.p + 1]))) {
      const arg = await sh.expandSingle(items[this.p + 1], io);
      this.p += 2;
      return testUnary(sh, u === '-a' ? '-e' : u === '-h' ? '-L' : u, arg);
    }
    const left = items[this.p++];
    if (this.p < items.length && this.isBinary(items[this.p])) {
      const op = literal(items[this.p++])!;
      if (this.p >= items.length) throw new ShellError(`unexpected argument to conditional binary operator \`${op}'`, 2);
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
        case '-ef': return sh.resolvePath(l) === sh.resolvePath(await sh.expandSingle(right, io)) && sh.fs.exists(sh.resolvePath(l));
        default: {
          const a = evalArith(sh, l), b = evalArith(sh, await sh.expandSingle(right, io));
          return op === '-eq' ? a === b : op === '-ne' ? a !== b : op === '-lt' ? a < b : op === '-le' ? a <= b : op === '-gt' ? a > b : a >= b;
        }
      }
    }
    return (await sh.expandSingle(left, io)).length > 0;
  }
}

// ---------- brace expansion ----------

const MAX_BRACE_ITEMS = 10_000;
const PLACEHOLDER = 0xe000;

/** a{b,c}d, {1..10}, {01..05}, {a..e}, {0..10..2}; a brace may span an expansion: {$x,y}. */
function braceExpand(w: Word): Word[] {
  if (!w.parts.some(p => p.k === 'lit' && !p.q && p.text.includes('{'))) return [w];
  // everything but unquoted text becomes one placeholder character, so braces work on plain text
  let text = '';
  const opaque: WordPart[] = [];
  for (const p of w.parts) {
    if (p.k === 'lit' && !p.q) text += p.text;
    else { text += String.fromCharCode(PLACEHOLDER + opaque.length); opaque.push(p); }
  }
  if (opaque.length > 0x1000) return [w];
  const results = expandBraceText(text);
  if (results.length === 1 && results[0] === text) return [w];
  return results.map(t => {
    const parts: WordPart[] = [];
    let litText = '';
    for (const c of t) {
      const code = c.charCodeAt(0);
      if (code < PLACEHOLDER || code >= PLACEHOLDER + opaque.length) { litText += c; continue; }
      if (litText) { parts.push({ k: 'lit', text: litText, q: false }); litText = ''; }
      parts.push(opaque[code - PLACEHOLDER]);
    }
    if (litText || parts.length === 0) parts.push({ k: 'lit', text: litText, q: false });
    return { parts };
  });
}

function expandBraceText(text: string): string[] {
  for (let open = text.indexOf('{'); open >= 0; open = text.indexOf('{', open + 1)) {
    if (open > 0 && text[open - 1] === '$') continue;
    const close = findClose(text, open);
    if (close < 0) continue;
    const alts = alternatives(text.slice(open + 1, close));
    if (!alts) continue;
    const results: string[] = [];
    for (const alt of alts) {
      results.push(...expandBraceText(text.slice(0, open) + alt + text.slice(close + 1)));
      if (results.length > MAX_BRACE_ITEMS) throw new ShellError('brace expansion: too many items');
    }
    return results;
  }
  return [text];
}

function findClose(s: string, open: number): number {
  let depth = 0;
  for (let i = open; i < s.length; i++) {
    if (s[i] === '{') depth++;
    else if (s[i] === '}' && --depth === 0) return i;
  }
  return -1;
}

function alternatives(inner: string): string[] | null {
  let m = /^(-?\d+)\.\.(-?\d+)(?:\.\.(-?\d+))?$/.exec(inner);
  if (m) {
    const a = Number(m[1]), b = Number(m[2]);
    let step = m[3] ? Math.abs(Number(m[3])) : 1;
    if (step === 0) step = 1;
    const padded = (s: string) => s.replace(/^-/, '').startsWith('0') && s.length > 1;
    const pad = padded(m[1]) || padded(m[2]);
    const width = Math.max(m[1].length, m[2].length);
    if (Math.abs(b - a) / step > MAX_BRACE_ITEMS) throw new ShellError('brace expansion: too many items');
    const list: string[] = [];
    for (let v = a; a <= b ? v <= b : v >= b; v += a <= b ? step : -step) {
      const s = String(Math.abs(v));
      list.push(pad ? (v < 0 ? '-' + s.padStart(width - 1, '0') : s.padStart(width, '0')) : String(v));
    }
    return list;
  }
  m = /^([a-zA-Z])\.\.([a-zA-Z])(?:\.\.(-?\d+))?$/.exec(inner);
  if (m) {
    const a = m[1].charCodeAt(0), b = m[2].charCodeAt(0);
    const step = m[3] ? Math.max(1, Math.abs(Number(m[3]))) : 1;
    const list: string[] = [];
    for (let c = a; a <= b ? c <= b : c >= b; c += a <= b ? step : -step) list.push(String.fromCharCode(c));
    return list;
  }
  const parts: string[] = [];
  let depth = 0, start = 0;
  for (let i = 0; i < inner.length; i++) {
    if (inner[i] === '{') depth++;
    else if (inner[i] === '}') depth--;
    else if (inner[i] === ',' && depth === 0) { parts.push(inner.slice(start, i)); start = i + 1; }
  }
  parts.push(inner.slice(start));
  return parts.length < 2 ? null : parts;
}

export { sizeText };
