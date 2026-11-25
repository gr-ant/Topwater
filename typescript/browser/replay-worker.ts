// Web Worker for the recording viewer: holds one recording and rebuilds the machine as of any turn. A turn's machine
// comes from the recording's own snapshot when it has one; otherwise from the nearest earlier machine, re-running the
// calls in between in this (TypeScript) Topwater. Re-running can't be interrupted from inside, so the page terminates
// this worker if an answer takes too long.
import { Recording, ShellSnapshot, type RecordedCall } from '../src/index.js';
import type { VfsEntry } from '../src/vfs.js';
import { diffLines } from './diff.js';
import type { CallInfo, Change, FileView, ReplayReply, ReplayRequest, TreeEntry, TurnView } from './replay-protocol.js';

let recording: Recording | null = null;
/** the machine after call i (-1: the start) */
const states = new Map<number, ShellSnapshot>();
/** calls this worker re-ran: whether the output matched the recording */
const reran = new Map<number, { matches: boolean; stdout: string; stderr: string; exitCode: number }>();
const MAX_TEXT = 256 << 10;
/** recordings may come from anyone: tighter than the defaults, so one can't exhaust the tab (the page also kills slow answers) */
const VIEWER_LIMITS = { maxFileSystemBytes: 32 << 20, maxStringBytes: 8 << 20, maxExtractBytes: 16 << 20, maxCallDurationMs: 30_000 };

const post = (m: ReplayReply, transfer: Transferable[] = []) => (self as unknown as Worker).postMessage(m, transfer);

async function load(text: string, sidecars: Record<string, Uint8Array>) {
  states.clear(); reran.clear();
  const byName = (path: string) => {
    const name = path.slice(path.lastIndexOf('/') + 1);
    const data = sidecars[path] ?? sidecars[name];
    if (!data) throw new Error(`this recording keeps its snapshots in separate files: open ${name} together with it`);
    return Promise.resolve(data);
  };
  recording = await Recording.load(text, byName, VIEWER_LIMITS);
  states.set(-1, recording.start);
  const calls: CallInfo[] = recording.calls.map(c => ({
    index: c.index, command: c.command, stdout: c.stdout, stderr: c.stderr, exitCode: c.exitCode, trailer: c.trailer, elapsedMs: c.elapsedMs, hasSnapshot: !!c.snapshot,
  }));
  const o = recording.options;
  return {
    calls, metadata: recording.metadata, topwater: recording.topwater ?? '', created: recording.created, appCommands: recording.appCommands,
    deterministic: recording.isDeterministic, user: recording.start.user, hostname: recording.start.hostname,
    startTime: o.startTime ?? null, randomSeed: o.randomSeed ?? null,
  };
}

/** The machine after call `index`, and how it was obtained. */
async function stateAt(index: number): Promise<{ snap: ShellSnapshot; source: TurnView['source'] }> {
  const rec = recording!;
  if (index >= 0 && rec.calls[index].snapshot) states.set(index, rec.calls[index].snapshot!);
  if (states.has(index)) return { snap: states.get(index)!, source: sourceOf(index) };
  let from = index - 1;
  while (!states.has(from) && !(from >= 0 && rec.calls[from].snapshot)) from--;
  if (!states.has(from)) states.set(from, rec.calls[from].snapshot!);
  const shell = rec.createShell(undefined, states.get(from)!);
  for (let i = from + 1; i <= index; i++) {
    const call: RecordedCall = rec.calls[i];
    const r = await shell.run(call.command);
    reran.set(i, { matches: r.stdout === call.stdout && r.stderr === call.stderr && r.exitCode === call.exitCode, stdout: r.stdout, stderr: r.stderr, exitCode: r.exitCode });
    states.set(i, ShellSnapshot.capture(shell));
  }
  return { snap: states.get(index)!, source: sourceOf(index) };
}

function sourceOf(index: number): TurnView['source'] {
  if (index < 0) return { kind: 'start' };
  if (recording!.calls[index].snapshot) return { kind: 'snapshot' };
  let from = index - 1;
  while (from >= 0 && !recording!.calls[from].snapshot) from--;
  const r = reran.get(index);
  return { kind: 'rerun', from, matches: r?.matches ?? true, stdout: r?.stdout, stderr: r?.stderr, exitCode: r?.exitCode };
}

const sizeOf = (e: VfsEntry) => e.isDirectory ? 0 : e.reportedSize ?? e.data?.length ?? new TextEncoder().encode(e.text ?? '').length;
const sameContent = (a: VfsEntry, b: VfsEntry) => {
  if (a.text !== null || b.text !== null) return a.text === b.text;
  const x = a.data!, y = b.data!;
  if (x === y) return true;
  if (x.length !== y.length) return false;
  for (let i = 0; i < x.length; i++) if (x[i] !== y[i]) return false;
  return true;
};

async function turn(index: number): Promise<TurnView> {
  const { snap, source } = await stateAt(index);
  const before = index >= 0 ? (await stateAt(index - 1)).snap : null;
  const tree: TreeEntry[] = snap.entries.map(e => [e.path, e.isDirectory, e.mode, e.owner, e.modified, sizeOf(e)]);
  const changes: Change[] = [];
  if (before) {
    const old = new Map(before.entries.map(e => [e.path, e]));
    const now = new Map(snap.entries.map(e => [e.path, e]));
    for (const e of snap.entries) {
      const o = old.get(e.path);
      if (stubChurn(o, e)) continue;
      if (!o) changes.push({ path: e.path, kind: 'added', isDirectory: e.isDirectory });
      else if (o.isDirectory !== e.isDirectory) changes.push({ path: e.path, kind: 'replaced', isDirectory: e.isDirectory });
      else if (!e.isDirectory && !sameContent(o, e)) changes.push({ path: e.path, kind: 'modified', isDirectory: false });
      else if (o.mode !== e.mode || o.owner !== e.owner) changes.push({ path: e.path, kind: 'permissions', isDirectory: e.isDirectory, detail: `${perm(o)} → ${perm(e)}` });
    }
    for (const o of before.entries) if (!now.has(o.path) && !stubChurn(o, undefined)) changes.push({ path: o.path, kind: 'removed', isDirectory: o.isDirectory });
  }
  // the agent rarely means the program stubs or /proc; keep them, but list the rest first
  changes.sort((a, b) => Number(isSystem(a.path)) - Number(isSystem(b.path)) || (a.path < b.path ? -1 : 1));
  return { index, tree, changes, source, cwd: snap.state.cwd };
}

// a program stub in /usr/bin or /bin appearing or going: the two ports have slightly different commands, so a turn re-run
// here (or a snapshot from the other port) lines the stubs up with this port's; that is no change the agent made
const isStub = (e: VfsEntry | undefined) => !!e && !e.isDirectory && /^\/(usr\/)?bin\/[^/]+$/.test(e.path) &&
  (e.text !== null ? e.text.startsWith('\u007fELF') : e.data!.length >= 4 && e.data![0] === 0x7f && e.data![1] === 0x45 && e.data![2] === 0x4c && e.data![3] === 0x46);
const stubChurn = (o: VfsEntry | undefined, e: VfsEntry | undefined) => (isStub(o) || !o) && (isStub(e) || !e) && (isStub(o) || isStub(e));
const perm = (e: VfsEntry) => `${e.mode.toString(8).padStart(4, '0')} ${e.owner}`;
const isSystem = (p: string) => /^\/(proc|sys|dev|usr|bin|etc)(\/|$)/.test(p);

function view(e: VfsEntry | undefined): FileView {
  if (!e) return { kind: 'missing' };
  if (e.isDirectory) return { kind: 'directory' };
  if (e.text !== null && !e.text.includes('\0')) {
    const truncated = e.text.length > MAX_TEXT;
    return { kind: 'text', text: truncated ? e.text.slice(0, MAX_TEXT) : e.text, truncated, size: sizeOf(e) };
  }
  const bytes = e.data ?? new TextEncoder().encode(e.text ?? '');
  return { kind: 'binary', size: bytes.length, head: bytes.slice(0, 512) };
}

async function file(index: number, path: string): Promise<FileView> {
  const { snap } = await stateAt(index);
  return view(snap.entries.find(e => e.path === path));
}

async function diff(index: number, path: string) {
  const after = view((await stateAt(index)).snap.entries.find(e => e.path === path));
  const before = index >= 0 ? view((await stateAt(index - 1)).snap.entries.find(e => e.path === path)) : { kind: 'missing' as const };
  const text = (v: FileView) => v.kind === 'text' ? v.text : v.kind === 'missing' ? '' : null;
  const a = text(before), b = text(after);
  if (a === null || b === null || (before.kind === 'text' && before.truncated) || (after.kind === 'text' && after.truncated))
    return { binary: true, before: before.kind, after: after.kind, hunks: null };
  return { binary: false, before: before.kind, after: after.kind, hunks: diffLines(a, b) };
}

self.onmessage = async (ev: MessageEvent<ReplayRequest>) => {
  const m = ev.data;
  try {
    let value: unknown, transfer: Transferable[] = [];
    switch (m.t) {
      case 'load': value = await load(m.text, m.sidecars ?? {}); break;
      case 'turn': value = await turn(m.index); break;
      case 'file': value = await file(m.index, m.path); break;
      case 'diff': value = await diff(m.index, m.path); break;
      case 'fork': {
        const zip = await (await stateAt(m.index)).snap.save();
        value = zip; transfer = [zip.buffer as ArrayBuffer];
        break;
      }
    }
    post({ id: m.id, ok: true, value }, transfer);
  } catch (e) {
    post({ id: m.id, ok: false, error: e instanceof Error ? e.message : String(e) });
  }
};
