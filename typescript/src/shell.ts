// The Shell class: state, constructor, clock, registration, run and limits; its other parts are in shell/. See docs/architecture.md.
import { VirtualFileSystem, ShellError } from './vfs.js';
import { StdinBuffer, LimitExceeded, DeadlineExceeded, VirtualTimeout, tooLarge, sizeText, type CommandHandler, type RegisteredCommand, type Writer, type Deadline } from './command.js';
import { parse, ParseError, type Word } from './parser.js';
import type { ArithHost } from './arith.js';
import { printable } from './bytes.js';
import { COMMANDS } from './spec.generated.js';
import { registerAll } from './commands/index.js';
import { registerApi, type ApiEndpoint } from './api.js';
import { WebSession, defaultTransport, type WebPolicy } from './web.js';
import { registerTools, mountMcp, type ToolSpec, type ToolInvoker, type McpClientLike, type McpMountOptions } from './tools.js';
import { type ShellOptions, DEFAULTS, ShellResult, type Io, ShellArray, LineAbort, LoopControl, ReturnSignal, ExitSignal, type FunctionDef, EXECUTABLE_STUB } from './shell/types.js';
import { ExecutionPart } from './shell/execution.js';
import { RedirectionPart, BufferWriter, SwitchWriter } from './shell/redirection.js';
import { InvocationPart } from './shell/invocation.js';
import { VariablesPart } from './shell/variables.js';
import { ExpansionPart } from './shell/expansion.js';
import { SessionPart } from './shell/session.js';
import { Mounts, type MountSource, type MountMode, type MountInfo, type MountChange, type MountApplyResult } from './mounts.js';
import { registerPython, pythonRuntime, type PythonOptions } from './commands/python.js';

// A JS regex match can't be interrupted, so an agent's catastrophically backtracking pattern (grep -E '(a|aa)+$') could
// hang the call. This V8 flag switches such a match to V8's linear-time engine (same results); patterns with
// backreferences have no linear engine and remain a risk. It is process-wide and only changes speed, never results.
// Node only (looked up at run time, so bundles for browsers carry no Node import); a browser host runs the shell in a
// Web Worker and terminates it at the time limit instead.
(globalThis as { process?: { getBuiltinModule?: (id: string) => { setFlagsFromString(f: string): void } | undefined } })
  .process?.getBuiltinModule?.('node:v8')?.setFlagsFromString('--enable-experimental-regexp-engine-on-excessive-backtracks');

const SYSTEM_DIRS = [
  '/boot', '/lib', '/lib64', '/media', '/mnt', '/proc/4242', '/proc/tty/driver', '/run/lock', '/sbin', '/srv', '/sys',
  '/etc/profile.d', '/etc/skel', '/usr/games', '/usr/include', '/usr/lib64', '/usr/libexec', '/usr/sbin', '/usr/src',
  '/usr/local/etc', '/usr/local/games', '/usr/local/include', '/usr/local/lib', '/usr/local/sbin', '/usr/local/share/man', '/usr/local/src',
  '/usr/share/base-files', '/usr/share/bash-completion', '/usr/share/common-licenses', '/usr/share/doc', '/usr/share/info', '/usr/share/locale',
  '/usr/share/man', '/usr/share/misc', '/usr/share/terminfo',
  '/var/backups', '/var/cache/apt/archives/partial', '/var/cache/debconf', '/var/cache/ldconfig', '/var/lib/apt', '/var/lib/dpkg', '/var/lib/misc', '/var/lib/pam', '/var/lib/systemd',
  '/var/local', '/var/mail', '/var/opt', '/var/spool',
];

// Ubuntu's files, rewritten in our own words with the same effect: a login shell reads /etc/profile, which runs
// /etc/profile.d/*.sh; an interactive one reads ~/.bashrc; a non-interactive one (every tool call) reads neither
const OS_RELEASE = "PRETTY_NAME=\"Ubuntu 24.04.1 LTS\"\nNAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\nVERSION=\"24.04.1 LTS (Noble Numbat)\"\nVERSION_CODENAME=noble\nID=ubuntu\nID_LIKE=debian\nHOME_URL=\"https://www.ubuntu.com/\"\nSUPPORT_URL=\"https://help.ubuntu.com/\"\nBUG_REPORT_URL=\"https://bugs.launchpad.net/ubuntu/\"\nPRIVACY_POLICY_URL=\"https://www.ubuntu.com/legal/terms-and-policies/privacy-policy\"\nUBUNTU_CODENAME=noble\nLOGO=ubuntu-logo\n";
const ETC_PROFILE = "# Read by every login shell (bash -l, sh -l).\n\n# interactive bash: the shared settings in /etc/bash.bashrc\nif [ \"${PS1-}\" ] && [ \"${BASH-}\" ] && [ -f /etc/bash.bashrc ]; then\n  . /etc/bash.bashrc\nfi\n\n# then each script in /etc/profile.d\nif [ -d /etc/profile.d ]; then\n  for i in /etc/profile.d/*.sh; do\n    [ -r \"$i\" ] && . \"$i\"\n  done\n  unset i\nfi\n";
const ETC_BASHRC = "# Shared settings for interactive bash shells.\n\n# nothing to do for scripts and bash -c\n[ -z \"${PS1-}\" ] && return\n\nshopt -s checkwinsize\nPS1='\\u@\\h:\\w\\$ '\n";
const DOT_PROFILE = "# Read by login shells, unless ~/.bash_profile or ~/.bash_login exists.\n\n# bash: also read ~/.bashrc\nif [ -n \"$BASH_VERSION\" ] && [ -f \"$HOME/.bashrc\" ]; then\n    . \"$HOME/.bashrc\"\nfi\n\n# your own programs, when you have them\nfor d in \"$HOME/bin\" \"$HOME/.local/bin\"; do\n    [ -d \"$d\" ] && PATH=\"$d:$PATH\"\ndone\nunset d\n";
const DOT_BASH_LOGOUT = "# Run when a login shell exits.\n";
const DOT_BASHRC = "# Settings for interactive bash shells.\n\n# scripts and bash -c stop here\ncase $- in\n    *i*) ;;\n      *) return;;\nesac\n\n# history: skip duplicates and lines that start with a space\nHISTCONTROL=ignoreboth\nHISTSIZE=1000\nHISTFILESIZE=2000\nshopt -s histappend checkwinsize\n\nPS1='\\u@\\h:\\w\\$ '\n\n# colour and shorthands\nalias ls='ls --color=auto'\nalias grep='grep --color=auto'\nalias ll='ls -alF'\nalias la='ls -A'\nalias l='ls -CF'\n\n[ -f ~/.bash_aliases ] && . ~/.bash_aliases\n";
const DAY = 86_400_000;

export interface Shell extends ExecutionPart, RedirectionPart, InvocationPart, VariablesPart, ExpansionPart, SessionPart {}

/**
 * A simulated bash session. Give the model one "bash" tool (see BashTool) and pass its command to `run`.
 * Shell state (cwd, variables, functions, aliases, files) persists between calls.
 */
export class Shell implements ArithHost {
  // ---------- state and constructor ----------
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
  /** @internal bash's hash table: name -> [path found on PATH, lookups since]. */ hashed = new Map<string, [string, number]>();
  /** @internal */ hashedFor: string | undefined; // the PATH the table was filled from: any change to PATH empties it
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

  /** @internal */ builtinNames: Set<string> | null = null;
  /** @internal */ readonly locals: Map<string, { value?: string; attrs?: string }>[] = [];
  /** @internal */ readonly localArrays: Map<string, ShellArray | undefined>[] = [];
  /** @internal */ readonly unexported = new Set<string>();
  /** @internal */ epoch = 0;
  /** @internal */ subshell = 0;
  /** @internal */ condDepth = 0;
  /** @internal */ cwdPath = '/';
  /** @internal */ ifsUnset = false;
  /** @internal */ lineno = 0;
  /** @internal Added to the line numbers of text parsed on its own (eval, $(...)), which count from the line it is on. */
  lineBase = 0;
  /** @internal */ lastBg = 0;
  /** @internal */ nextPid = 4300;
  /** @internal */ lastArg = '';
  /** @internal */ pipeStatus: number[] = [];
  /** @internal */ readonly funcNames: string[] = [];
  /** @internal */ readonly sources: string[] = [];
  /** @internal */ readonly errWhere: { name: string; file: boolean }[] = []; // whose name starts bash's own errors: a script file, bash -c, a function's origin
  /** @internal */ readonly funcWhere = new Map<string, string>();
  /** @internal */ arg0 = 'bash';
  /** @internal */ readonly jobs = new Map<number, number>();
  /** @internal */ secondsBase = 0;
  /** @internal */ virtualClock: number | undefined;
  private virtualDeadline: number | undefined;
  /** @internal */ random: bigint | undefined;
  /** @internal */ totalIterations = 0;
  /** @internal */ scriptDepth = 0;
  /** @internal */ arithDepth = 0;
  /** @internal */ variableChars = 0;
  /** @internal */ procSubCount = 0;
  /** @internal */ inErrTrap = false;
  /** @internal */ inDebugTrap = false;
  /** @internal */ callDepth = 0;
  /** @internal the innermost deadline (the call's, or a `timeout` command's) */
  deadline: Deadline = { at: Infinity };
  /** @internal */ readonly paramWords = new Map<string, Word>();
  /** @internal The web access in force (allowWeb), or undefined: no network. */
  web?: WebSession;
  /** @internal mount(): outside sources shown as directories */
  readonly mountTable: Mounts = new Mounts(this);

  constructor(options: Partial<ShellOptions> = {}) {
    this.options = { ...DEFAULTS, ...options };
    this.secondsBase = this.options.startTime ?? Date.now();
    const fs = this.fs = options.fileSystem ?? new VirtualFileSystem();
    fs.capacity = this.options.maxFileSystemBytes;
    const user = this.options.user, home = '/home/' + user;
    this.agentFs = fs.as(user);
    this.virtualClock = this.options.startTime;
    if (this.options.randomSeed !== undefined) this.random = BigInt.asUintN(64, BigInt(this.options.randomSeed));
    fs.clock = () => this.now();
    const now = new Date(this.now());
    this.installedAt = Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate()) - 142 * DAY + (9 * 60 + 13) * 60_000;
    {
      for (const dir of [home, '/tmp', '/var/tmp', '/usr/bin', '/bin', '/usr/local/bin', '/opt', '/etc', '/dev/fd', '/var/log']) fs.mkdir(dir, true);
      if (!fs.exists('/etc/os-release')) {
        fs.mkdir('/usr/lib', true);
        fs.writeText('/usr/lib/os-release', OS_RELEASE);
        fs.createSymlink('/etc/os-release', '../usr/lib/os-release');
      }
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
    for (const dir of ['/bin', '/usr', '/etc', '/dev', '/opt', '/root', '/var/log', '/var/lib', '/var/cache', '/var/backups', '/var/local', '/var/mail', '/var/opt', '/var/spool', '/run', '/boot', '/lib', '/lib64', '/media', '/mnt', '/sbin', '/srv'])
      fs.setModified(dir, this.installedAt, true);
    for (const link of ['/var/run', '/var/lock']) if (fs.isSymlink(link)) fs.setModifiedTime(link, this.installedAt, false);
    for (const p of ['/', '/home', '/var', home + '/.bashrc', home + '/.profile', home + '/.bash_logout']) fs.setModified(p, this.installedAt);
  }

  /**
   * The ordinary clutter of a Linux install. A model that looks around and finds four files in /etc, no dotfiles
   * and no /proc starts wondering what kind of machine it is on, and spends its turns investigating.
   */
  private seedSystemFiles(user: string, home: string) {
    const fs = this.fs, host = this.options.hostname;
    for (const dir of SYSTEM_DIRS) fs.mkdir(dir, true);
    // what an ordinary user can't read, so `find /` reports "Permission denied" and exits 1 as on Ubuntu
    for (const dir of ['/var/cache/ldconfig', '/var/cache/apt/archives/partial', '/proc/tty/driver']) fs.chmod(dir, 0o700);
    fs.chmod('/run/lock', 0o1777);
    const seed = (path: string, content: string) => { if (!fs.exists(path)) fs.writeText(path, content); };
    const link = (path: string, target: string) => { if (!fs.exists(path) && !fs.isSymlink(path)) fs.createSymlink(path, target); };
    link('/var/run', '/run');
    link('/var/lock', '/run/lock');
    link('/usr/local/man', 'share/man');
    fs.writeText('/etc/hostname', host + '\n');
    seed('/etc/issue', 'Ubuntu 24.04.1 LTS \\n \\l\n\n');
    seed('/etc/issue.net', 'Ubuntu 24.04.1 LTS\n');
    seed('/etc/passwd', `root:x:0:0:root:/root:/bin/bash\n${user}:x:1000:1000:${user}:${home}:/bin/bash\n`);
    fs.writeText('/dev/null', '');
    seed('/etc/hosts', `127.0.0.1\tlocalhost\n127.0.1.1\t${host}\n\n::1\tip6-localhost ip6-loopback\nfe00::0\tip6-localnet\nff02::1\tip6-allnodes\nff02::2\tip6-allrouters\n`);
    seed('/etc/group', `root:x:0:\nadm:x:4:\nsudo:x:27:\nusers:x:100:\n${user}:x:1000:\n`);
    seed('/etc/shells', '# Shells a user may have as a login shell\n/bin/sh\n/usr/bin/sh\n/bin/bash\n/usr/bin/bash\n/bin/rbash\n/usr/bin/rbash\n/usr/bin/dash\n');
    seed('/etc/debian_version', 'trixie/sid\n');
    seed('/var/lib/shells.state', '');
    seed('/etc/profile', ETC_PROFILE);
    seed('/etc/bash.bashrc', ETC_BASHRC);
    seed('/etc/profile.d/01-locale-fix.sh', '# Use the C.UTF-8 locale when none is set.\nexport LANG="${LANG:-C.UTF-8}"\n');
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
    // the shell's own process: /proc/self points at it ($$ is 4242)
    const uid = user === 'root' ? 0 : 1000;
    seed('/proc/4242/comm', 'bash\n');
    seed('/proc/4242/cmdline', 'bash\0');
    seed('/proc/4242/status', `Name:\tbash\nState:\tS (sleeping)\nPid:\t4242\nPPid:\t1\nUid:\t${uid}\t${uid}\t${uid}\t${uid}\nGid:\t${uid}\t${uid}\t${uid}\t${uid}\n`);
    link('/proc/self', '4242');

    for (const dir of ['/etc/skel', home]) {
      seed(dir + '/.profile', DOT_PROFILE);
      seed(dir + '/.bash_logout', DOT_BASH_LOGOUT);
      seed(dir + '/.bashrc', DOT_BASHRC);
    }
  }

  get cwd(): string { return this.cwdPath; }
  set cwd(value: string) {
    const abs = this.resolvePath(value);
    if (!this.fs.isDirectory(abs)) throw new ShellError(`${value}: No such file or directory`);
    this.cwdPath = abs;
    this.environment.set('PWD', abs);
  }

  /** An absolute path for `path` from the cwd; ".." goes up from where the path really is, past symbolic links, as the kernel resolves it. */
  resolvePath(path: string): string { return this.agentFs.normalizePhysical(this.cwdPath, path); }

  /** @internal bash's prefix for its own errors: "bash: line 3: ", "./s.sh: line 2: ", or "environment: line 1: " in a
   *  function defined in a bash -c string. */
  get errPrefix(): string { return `${this.errWhere.at(-1)?.name ?? 'bash'}: line ${Math.max(this.lineno, 1)}: `; }

  /** @internal The IFS that splitting uses: the default when it is unset. */
  get ifs(): string { return this.environment.get('IFS') ?? ' \t\n'; }

  /** @internal "$*" and "${a[*]}" join with the first character of IFS (a space when IFS is unset). */
  get ifsJoiner(): string { const ifs = this.environment.get('IFS'); return ifs === undefined ? ' ' : ifs.slice(0, 1); }

  /** @internal The set -o options this shell has, and whether each is on. */
  get optionList(): [string, boolean][] {
    return [['allexport', false], ['braceexpand', true], ['emacs', false], ['errexit', this.errexit], ['errtrace', false], ['functrace', false],
      ['hashall', true], ['histexpand', false], ['history', false], ['ignoreeof', false], ['interactive-comments', true], ['keyword', false],
      ['monitor', false], ['noclobber', this.noclobber], ['noexec', false], ['noglob', this.noglob], ['nolog', false], ['notify', false],
      ['nounset', this.nounset], ['onecmd', false], ['physical', false], ['pipefail', this.pipefail], ['posix', false], ['privileged', false],
      ['verbose', false], ['vi', false], ['xtrace', this.xtrace]];
  }

  /** @internal $-: the single-letter options that are on. */
  get optionFlags() {
    return (this.errexit ? 'e' : '') + (this.noglob ? 'f' : '') + 'h' + (this.nounset ? 'u' : '') + (this.xtrace ? 'x' : '') + 'B' + (this.noclobber ? 'C' : '') + 'c';
  }

  /** @internal The virtual clock's time, or undefined when the shell has none; setting it applies only to a shell that has one. */
  get virtualNow(): number | undefined { return this.virtualClock; }
  set virtualNow(ms: number | undefined) { if (this.virtualClock !== undefined && ms !== undefined) this.virtualClock = ms; }

  /** @internal The seeded generator's state (splitmix64), or undefined when the shell is not seeded; setting it applies only to a seeded shell. */
  get randomState(): bigint | undefined { return this.random; }
  set randomState(s: bigint | undefined) { if (this.random !== undefined && s !== undefined) this.random = BigInt.asUintN(64, s); }

  /** @internal */ get inFunction() { return this.locals.length > 0; }
  /** @internal */ get funcNameStack(): readonly string[] { return this.funcNames; }

  // ---------- clock and random ----------

  /** @internal The machine's time, ms since the epoch (UTC): the virtual clock when `startTime` is set, else the host's. */
  now(): number { return this.virtualClock ?? Date.now(); }

  /** @internal `sleep` on the virtual clock: moves time on (to the deadline of an enclosing `timeout`, which then fires). */
  sleepVirtual(ms: number) {
    const now = this.virtualClock!;
    if (this.virtualDeadline !== undefined && now + ms >= this.virtualDeadline) { this.virtualClock = this.virtualDeadline; throw new VirtualTimeout(); }
    this.virtualClock = now + ms;
  }

  /** @internal `timeout` on the virtual clock: runs `body` with the limit in virtual time; null when it timed out. */
  async withVirtualTimeout(ms: number, body: () => Promise<number>): Promise<number | null> {
    if (this.virtualClock === undefined) return body();
    const saved = this.virtualDeadline, mine = this.virtualClock + ms;
    if (saved === undefined || mine < saved) this.virtualDeadline = mine;
    try { return await body(); } catch (e) {
      if (e instanceof VirtualTimeout && this.virtualClock! >= mine) return null;
      throw e;
    } finally { this.virtualDeadline = saved; }
  }

  /** @internal A random number in [0, max): from the seeded generator or Math.random. */
  nextRandom(max: number): number {
    if (this.random === undefined) return Math.floor(Math.random() * max);
    const M = (1n << 64n) - 1n;
    const state = this.random = (this.random + 0x9E3779B97F4A7C15n) & M;
    let z = state;
    z = ((z ^ (z >> 30n)) * 0xBF58476D1CE4E5B9n) & M;
    z = ((z ^ (z >> 27n)) * 0x94D049BB133111EBn) & M;
    return Number((z ^ (z >> 31n)) % BigInt(max));
  }

  // ---------- registration ----------

  /** Adds a command the agent can run (and lists it in /usr/bin and `help`). Returns this shell. */
  register(name: string, handler: CommandHandler, description = '', usage?: string): this {
    const spec = COMMANDS.get(name);
    // built-ins take their summary and help from spec/ (spec.generated.ts)
    if (this.builtinNames === null && description === '' && usage === undefined && spec) { description = spec.summary; usage = spec.help ?? undefined; }
    this.commands.set(name, { name, description, usage, handler });
    this.builtinNames?.delete(name); // an app command replacing a built-in owns its own --help/--version
    this.writeProgramFile(name, this.builtinNames === null);
    return this;
  }

  /** @internal */ writeProgramFile(name: string, builtin: boolean) {
    const shellOnly = builtin && COMMANDS.get(name)?.programFile === false;
    if (shellOnly || name.includes('/') || name === '.' || name === '..' || name === ':' || !this.fs.isDirectory('/usr/bin')) return;
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

  /**
   * Lets the agent's `curl` and `wget` reach the hosts in `policy`, over https unless it allows http. Every URL and
   * redirect is checked against it; names are resolved once, private, loopback, link-local and other internal addresses
   * are refused, and the connection goes to the address that was checked.
   */
  allowWeb(policy: WebPolicy): this {
    if (!policy.transport) defaultTransport(); // throws where the checked connection cannot be made
    this.web = new WebSession(policy);
    return this;
  }

  /**
   * Gives the agent python3: CPython 3.14 compiled to WebAssembly, over this machine's files and nothing else
   * (docs/python.md). Needs the add-on (npm install topwater-python) or `options.artifacts`. The command exists at once;
   * the promise resolves once the interpreter is loaded and compiled (the first time in a process: about a second) and
   * rejects with PythonUnavailable when it can't run here. HTTP from Python goes through allowWeb's policy, or nowhere.
   */
  allowPython(options: PythonOptions = {}): Promise<this> {
    registerPython(this, options);
    this.builtinNames?.add('python3'); // a built-in from spec/: its --help and --version are the spec's
    return pythonRuntime(options).then(() => this);
  }

  /** Shows `source` as the directory `path`: mode "read-only", "staged" or "live" (docs/mounts.md). Lists the source before it resolves. */
  mount(path: string, source: MountSource, mode: MountMode = 'read-only'): Promise<void> { return this.mountTable.mount(path, source, mode); }

  /** Removes a mount and its pending changes; the mount point stays as an empty directory. */
  unmount(path: string): void { this.mountTable.unmount(path); }

  /** Every mount (path, mode, source type and id, why it is unavailable). */
  get mounts(): MountInfo[] { return [...this.mountTable.items.values()].map(m => m.info()); }

  /** The changes that would make a staged or live mount's source match its files, in order. */
  pendingChanges(path: string): MountChange[] { return this.mountTable.get(path).changes(); }

  /** Applies the pending changes, or the ones given (from pendingChanges), in order, stopping at the first conflict or failure. */
  applyChanges(path: string, changes?: MountChange[] | null): Promise<MountApplyResult> { return this.mountTable.get(path).apply(changes); }

  /** Drops a mount's pending changes, and the ones a loaded snapshot brought: its files are the source's again. */
  discardChanges(path: string): void { this.mountTable.discard(path); }

  /** The changes a snapshot loaded from a file brought for this mount (as untrusted as the file): not in the files, never
   * applied, until acceptSnapshotChanges. */
  snapshotChanges(path: string): MountChange[] { return [...this.mountTable.get(path).held]; }

  /** Puts a loaded snapshot's changes into the mount's files, where they become pending changes like the agent's. */
  acceptSnapshotChanges(path: string): Promise<void> { return this.mountTable.accept(path); }

  /** Lists the source again, keeping the pending changes. */
  refreshMount(path: string): Promise<void> { return this.mountTable.refresh(path); }

  unregister(name: string): boolean {
    for (const dir of ['/usr/bin/', '/bin/']) if (this.fs.isFile(dir + name)) this.fs.delete(dir + name);
    return this.commands.delete(name);
  }

  // ---------- run ----------

  /**
   * Runs one tool call: the model's command line. Never throws for anything the command does.
   * `onOutput`, if given, sees each write as it happens (for showing output live); the result still has all of it.
   */
  async run(commandLine: string, onOutput?: (text: string, stream: 'stdout' | 'stderr') => void): Promise<ShellResult> {
    this.history.push(commandLine);
    const stdout = new BufferWriter(this.options.maxStringBytes);
    const stderr = new BufferWriter(this.options.maxStringBytes);
    let code: number;
    let trailer: string | undefined;
    this.totalIterations = this.scriptDepth = this.callDepth = this.arithDepth = this.lineno = 0; // each call is a new bash -c: its lines count from 1
    this.web?.beginCall();
    const callDeadline: Deadline = { at: Date.now() + this.options.maxCallDurationMs };
    this.deadline = callDeadline;
    const tee = (w: Writer, stream: 'stdout' | 'stderr'): Writer => onOutput ? { write(t) { w.write(t); onOutput(printable(t), stream); } } : w;
    const io: Io = { stdin: new StdinBuffer(''), out: new SwitchWriter(tee(stdout, 'stdout')), err: new SwitchWriter(tee(stderr, 'stderr')) };
    const raw = (s: string) => { (stderr as unknown as { chunks: string[] }).chunks.push(s); stderr.length += s.length; };
    try {
      // shortcut: EXIT traps fire when the tool call ends (like `set -e`, they last one call), not at logout
      try {
        if (this.mountTable.items.size) await this.mountTable.prepare();
        code = await this.execTopLevel(parse(commandLine), io);
      } finally {
        await this.runExitTrap(io);
        if (this.mountTable.items.size && this.remainingMs() > 0) await this.mountTable.settle(io.err);
      }
    } catch (e) {
      if (e instanceof ParseError) { raw(`bash: ${e.message}\n`); code = 2; }
      else if (e instanceof ExitSignal) code = e.code;
      else if (e instanceof ReturnSignal) code = e.code;
      else if (e instanceof LoopControl) code = 0;
      else if (e instanceof LineAbort) code = 1;
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

  // ---------- limits ----------

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
}

for (const part of [ExecutionPart, RedirectionPart, InvocationPart, VariablesPart, ExpansionPart, SessionPart])
  for (const k of Object.getOwnPropertyNames(part.prototype))
    if (k !== 'constructor') Object.defineProperty(Shell.prototype, k, Object.getOwnPropertyDescriptor(part.prototype, k)!);

// ---------- re-exports ----------

export * from './shell/types.js';
export { splitFields } from './shell/expansion.js';
export { arrayRef } from './shell/variables.js';
export { BufferWriter } from './shell/redirection.js';
export { sizeText };

// ---------- helpers ----------

function stopped(why: string) {
  return `[stopped: ${why}. Running it again will hit the same limit. Files and variables changed before this point are kept.]`;
}
function seconds(ms: number) { return `${Number((ms / 1000).toFixed(3))} s`; }
