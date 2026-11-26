// The recording viewer's timelines: the recording as loaded (branch 0) and the branches typed after rewinding to one of
// its calls. Branch b's calls are its parent's up to and including `at`, then its own; a call's position is the same in
// every branch that shares it. The machine after any call comes from the nearest snapshot or cached machine before it,
// re-running the calls in between, so jumping around a long recording never rebuilds it from the start.
import { Recording, ShellSnapshot, type RecordedCall, type Shell, type ShellResult } from '../src/index.js';
import { changedPaths } from '../src/recording.js';
import type { VfsEntry } from '../src/vfs.js';
import type { Change } from './replay-protocol.js';

export interface Branch { parent: number; at: number; calls: RecordedCall[] }
/** call `i` (-1: the start) of branch `b` */
export interface Point { b: number; i: number }

export class Timelines {
  readonly branches: Branch[];
  /** calls this viewer re-ran, by key: whether the output matched the recording */
  readonly reran = new Map<string, { matches: boolean; stdout: string; stderr: string; exitCode: number }>();
  private readonly states = new Map<string, ShellSnapshot>();
  /** a shell at each branch's last call, so typing on a branch doesn't restore the machine every time */
  private readonly live = new Map<number, Shell>();
  /** whether the recording lists each call's changed files (a .topwater file does; older recordings may not) */
  private readonly tracksFiles: boolean;

  constructor(readonly recording: Recording) {
    this.branches = [{ parent: -1, at: -1, calls: recording.calls }];
    this.states.set('start', recording.start);
    this.tracksFiles = recording.calls.some(c => c.files !== undefined);
  }

  length(b: number): number { const br = this.branches[b]; return b === 0 ? br.calls.length : br.at + 1 + br.calls.length; }
  /** the branch that holds call `i` of branch `b` */
  owner(b: number, i: number): number { while (b > 0 && i <= this.branches[b].at) b = this.branches[b].parent; return b; }
  call(b: number, i: number): RecordedCall { const o = this.owner(b, i); return this.branches[o].calls[o === 0 ? i : i - this.branches[o].at - 1]; }
  key(b: number, i: number): string { return i < 0 ? 'start' : `${this.owner(b, i)}:${i}`; }

  private check(b: number, i: number) {
    if (!this.branches[b] || !Number.isInteger(i) || i < -1 || i >= this.length(b)) throw new RangeError(`no call ${i + 1} on branch ${b}`);
  }

  /** The machine after call `i` of branch `b`. */
  async stateAt(b: number, i: number): Promise<ShellSnapshot> {
    this.check(b, i);
    const k = this.key(b, i);
    const have = this.states.get(k) ?? (i >= 0 ? this.call(b, i).snapshot : undefined);
    if (have) { this.states.set(k, have); return have; }
    let from = i - 1;
    while (!this.states.has(this.key(b, from)) && !this.call(b, from).snapshot) from--;
    const shell = this.recording.createShell(undefined, await this.stateAt(b, from));
    for (let j = from + 1; j <= i; j++) {
      const c = this.call(b, j), r = await shell.run(c.command);
      this.reran.set(this.key(b, j), { matches: r.stdout === c.stdout && r.stderr === c.stderr && r.exitCode === c.exitCode, stdout: r.stdout, stderr: r.stderr, exitCode: r.exitCode });
      this.states.set(this.key(b, j), ShellSnapshot.capture(shell));
    }
    return this.states.get(k)!;
  }

  /** A new, empty branch that continues from call `at` of branch `parent`; its id. */
  fork(parent: number, at: number): number {
    this.check(parent, at);
    return this.branches.push({ parent, at, calls: [] }) - 1;
  }

  /** Runs `command` after the last call of branch `b` (not the recording itself) and adds it to the branch. */
  async run(b: number, command: string): Promise<{ call: RecordedCall; result: ShellResult }> {
    if (b <= 0 || !this.branches[b]) throw new RangeError(`no branch ${b}`);
    const last = this.length(b) - 1, before = await this.stateAt(b, last);
    const shell = this.live.get(b) ?? this.recording.createShell(undefined, before);
    this.live.delete(b); // until the call is done: if it throws, the next one starts again from the snapshot
    const time = shell.now(), t0 = performance.now();
    const result = await shell.run(command);
    const after = ShellSnapshot.capture(shell);
    const call: RecordedCall = {
      index: last + 1, command, stdout: result.stdout, stderr: result.stderr, exitCode: result.exitCode, trailer: result.trailer,
      elapsedMs: performance.now() - t0, time, files: changedPaths(before.entries, after.entries),
    };
    this.branches[b].calls.push(call);
    this.states.set(this.key(b, last + 1), after);
    this.live.set(b, shell);
    return { call, result };
  }

  /**
   * The paths that can differ between two points, from the calls' lists of changed files: the calls after the points'
   * last shared call, on each side. Null when some of those calls don't list their files (then every path is compared).
   */
  candidates(a: Point, z: Point): Set<string> | null {
    let shared = -1;
    while (shared < Math.min(a.i, z.i) && this.key(a.b, shared + 1) === this.key(z.b, shared + 1)) shared++;
    const paths = new Set<string>();
    for (const p of [a, z])
      for (let j = shared + 1; j <= p.i; j++) {
        const c = this.call(p.b, j);
        if (c.files === undefined && (this.owner(p.b, j) > 0 || !this.tracksFiles)) return null;
        for (const f of c.files ?? []) paths.add(f);
      }
    return paths;
  }

  /** What changed between the machines at two points. */
  async compare(a: Point, z: Point): Promise<Change[]> {
    const [x, y] = [await this.stateAt(a.b, a.i), await this.stateAt(z.b, z.i)];
    return diffMachines(x.entries, y.entries, this.candidates(a, z));
  }

  /**
   * Branch `b` as a recording of its own: the recording's calls up to the fork, then the branch's, with the recording's
   * start, options and metadata (plus where it was forked). The machines after the fork and after the last call are
   * checkpoints, so every port starts the branch's calls from the machine they ran on here and ends on the same machine.
   * shortcut: the branch's calls in between are re-run from the fork's checkpoint when a reader needs them; checkpoint
   * more of them if a port's re-run turns out to differ (each costs a whole machine against the file's snapshot caps).
   */
  async export(b: number, from: string): Promise<Uint8Array> {
    if (b <= 0 || !this.branches[b]) throw new RangeError(`no branch ${b}`);
    const src = this.recording, out = new Recording(src.start, src.options);
    out.topwater = src.topwater;
    out.appCommands = src.appCommands;
    const at = this.branches[b].at;
    out.metadata = { ...src.metadata, forkedFrom: from, forkedAfterCall: String(at + 1) };
    for (let i = 0; i < this.length(b); i++) {
      const c = this.call(b, i), own = this.owner(b, i) > 0;
      out.calls.push({ ...c, index: i, snapshot: i === at || i === this.length(b) - 1 ? await this.stateAt(b, i) : own ? undefined : c.snapshot });
    }
    return out.save();
  }
}

// ---------- comparing two machines ----------

/** a program stub in /usr/bin or /bin: the ports have slightly different commands, so one coming or going is no change the agent made */
const isStub = (e: VfsEntry | undefined) => !!e && !e.isDirectory && e.linkTarget === null && /^\/(usr\/)?bin\/[^/]+$/.test(e.path) &&
  (e.text !== null ? e.text.startsWith('\u007fELF') : !!e.data && e.data.length >= 4 && e.data[0] === 0x7f && e.data[1] === 0x45 && e.data[2] === 0x4c && e.data[3] === 0x46);
const stubChurn = (o: VfsEntry | undefined, e: VfsEntry | undefined) => (isStub(o) || !o) && (isStub(e) || !e) && (isStub(o) || isStub(e));
const perm = (e: VfsEntry) => `${e.mode.toString(8).padStart(4, '0')} ${e.owner}`;
export const isSystem = (p: string) => /^\/(proc|sys|dev|usr|bin|etc)(\/|$)/.test(p);

export function sameContent(a: VfsEntry, b: VfsEntry): boolean {
  if (a.linkTarget !== null || b.linkTarget !== null) return a.linkTarget === b.linkTarget;
  if (a.text !== null || b.text !== null) return a.text === b.text;
  const x = a.data, y = b.data;
  if (x === y) return true;
  if (!x || !y || x.length !== y.length) return false;
  for (let i = 0; i < x.length; i++) if (x[i] !== y[i]) return false;
  return true;
}

const byPath = new WeakMap<readonly VfsEntry[], Map<string, VfsEntry>>();
const index = (entries: readonly VfsEntry[]) => byPath.get(entries) ?? byPath.set(entries, new Map(entries.map(e => [e.path, e]))).get(entries)!;
const kindOf = (e: VfsEntry) => e.isDirectory ? 'dir' : e.linkTarget !== null ? 'link' : 'file';

/** The changes from machine `a` to machine `b`, looking only at `paths` when given; the agent's files first. */
export function diffMachines(a: readonly VfsEntry[], b: readonly VfsEntry[], paths: Iterable<string> | null = null): Change[] {
  const old = index(a), now = index(b);
  const changes: Change[] = [];
  for (const path of paths ?? new Set([...now.keys(), ...old.keys()])) {
    const o = old.get(path), e = now.get(path);
    if (stubChurn(o, e) || (!o && !e)) continue;
    const link = (x: VfsEntry) => x.linkTarget !== null ? { isLink: true } : {};
    if (!o) changes.push({ path, kind: 'added', isDirectory: e!.isDirectory, ...link(e!), detail: e!.linkTarget !== null ? `→ ${e!.linkTarget}` : modeNote(null, e!) });
    else if (!e) changes.push({ path, kind: 'removed', isDirectory: o.isDirectory, ...link(o) });
    else if (kindOf(o) !== kindOf(e)) changes.push({ path, kind: 'replaced', isDirectory: e.isDirectory, ...link(e), detail: `${kindOf(o)} → ${kindOf(e)}` });
    else if (!e.isDirectory && !sameContent(o, e))
      changes.push({ path, kind: 'modified', isDirectory: false, ...link(e), detail: e.linkTarget !== null ? `→ ${o.linkTarget} became → ${e.linkTarget}` : modeNote(o, e) });
    else if (o.mode !== e.mode || o.owner !== e.owner) changes.push({ path, kind: 'permissions', isDirectory: e.isDirectory, ...link(e), detail: `${perm(o)} → ${perm(e)}` });
  }
  return changes.sort((x, y) => Number(isSystem(x.path)) - Number(isSystem(y.path)) || (x.path < y.path ? -1 : 1));
}

/** a mode or owner change beside a content change; a new file's mode when it isn't the usual one */
function modeNote(o: VfsEntry | null, e: VfsEntry): string | undefined {
  if (!o) return e.isDirectory || e.mode === 0o644 ? undefined : `mode ${perm(e)}`;
  return o.mode !== e.mode || o.owner !== e.owner ? `and ${perm(o)} → ${perm(e)}` : undefined;
}
