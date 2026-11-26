// Web Worker for the recording viewer: holds one recording (a .topwater file, or an old .jsonl one) and the branches typed
// after rewinding it (timeline.ts), and rebuilds the machine as of any call. Re-running can't be interrupted from inside,
// so the page terminates this worker if an answer takes too long, then loads the recording and its branches again.
import { Recording } from '../src/index.js';
import type { VfsEntry } from '../src/vfs.js';
import { diffLines } from './diff.js';
import type { CallInfo, DiffView, FileView, Point, ReplayReply, ReplayRequest, TreeEntry, TurnView } from './replay-protocol.js';
import { Timelines, contentOf } from './timeline.js';

let t: Timelines | null = null;
const MAX_TEXT = 256 << 10;
/** recordings may come from anyone: tighter than the defaults, so one can't exhaust the tab (the page also kills slow answers) */
const VIEWER_LIMITS = { maxFileSystemBytes: 32 << 20, maxStringBytes: 8 << 20, maxExtractBytes: 16 << 20, maxCallDurationMs: 30_000 };

const post = (m: ReplayReply, transfer: Transferable[] = []) => (self as unknown as Worker).postMessage(m, transfer);
const info = (c: Recording['calls'][number]): CallInfo => ({
  index: c.index, command: c.command, stdout: c.stdout, stderr: c.stderr, exitCode: c.exitCode, trailer: c.trailer, elapsedMs: c.elapsedMs,
  hasSnapshot: !!c.snapshot, time: c.time, files: c.files,
});

async function load(data: Uint8Array, sidecars: Record<string, Uint8Array>) {
  t = null;
  const byName = (path: string) => {
    const name = path.slice(path.lastIndexOf('/') + 1);
    const data = sidecars[path] ?? sidecars[name];
    if (!data) throw new Error(`this recording keeps its snapshots in separate files: open ${name} together with it`);
    return Promise.resolve(data);
  };
  const recording = await Recording.load(data, byName, VIEWER_LIMITS);
  t = new Timelines(recording);
  const o = recording.options;
  return {
    calls: recording.calls.map(info), metadata: recording.metadata, topwater: recording.topwater ?? '', created: recording.created, appCommands: recording.appCommands,
    deterministic: recording.isDeterministic, user: recording.start.user, hostname: recording.start.hostname,
    startTime: o.startTime ?? null, randomSeed: o.randomSeed ?? null,
  };
}

function sourceOf({ b, i }: Point): TurnView['source'] {
  const tl = t!;
  if (i < 0) return { kind: 'start' };
  if (tl.call(b, i).snapshot) return { kind: 'snapshot' };
  const r = tl.reran.get(tl.key(b, i));
  if (!r && tl.owner(b, i) > 0) return { kind: 'typed' };
  let from = i - 1;
  while (from >= 0 && !tl.call(b, from).snapshot) from--;
  return { kind: 'rerun', from, matches: r?.matches ?? true, stdout: r?.stdout, stderr: r?.stderr, exitCode: r?.exitCode };
}

const sizeOf = (e: VfsEntry) => e.isDirectory || e.linkTarget !== null ? 0 : e.reportedSize ?? e.data?.length ?? new TextEncoder().encode(e.text ?? '').length;

async function turn(p: Point): Promise<TurnView> {
  const tl = t!, snap = await tl.stateAt(p.b, p.i);
  const tree: TreeEntry[] = snap.entries.map(e => [e.path, e.isDirectory, e.mode, e.owner, e.modified, sizeOf(contentOf(e, snap.entries))]);
  const changes = p.i >= 0 ? await tl.compare({ b: p.b, i: p.i - 1 }, p) : [];
  return { p, tree, changes, source: sourceOf(p), cwd: snap.state.cwd };
}

function view(e: VfsEntry | undefined): FileView {
  if (!e) return { kind: 'missing' };
  if (e.isDirectory) return { kind: 'directory' };
  if (e.linkTarget !== null) return { kind: 'link', target: e.linkTarget };
  if (e.text !== null && !e.text.includes('\0')) {
    const truncated = e.text.length > MAX_TEXT;
    return { kind: 'text', text: truncated ? e.text.slice(0, MAX_TEXT) : e.text, truncated, size: sizeOf(e) };
  }
  const bytes = e.data ?? new TextEncoder().encode(e.text ?? '');
  return { kind: 'binary', size: bytes.length, head: bytes.slice(0, 512) };
}

const entry = async (p: Point, path: string) => {
  const entries = (await t!.stateAt(p.b, p.i)).entries, e = entries.find(x => x.path === path);
  return e && contentOf(e, entries);
};

async function diff(a: Point, z: Point, path: string): Promise<DiffView> {
  const before = view(await entry(a, path)), after = view(await entry(z, path));
  const size = (v: FileView) => v.kind === 'text' || v.kind === 'binary' ? v.size : 0;
  const text = (v: FileView) => v.kind === 'text' && !v.truncated ? v.text : v.kind === 'missing' ? '' : null;
  const x = text(before), y = text(after);
  const base = { before: before.kind, after: after.kind, beforeSize: size(before), afterSize: size(after) };
  return x === null || y === null ? { ...base, binary: true, hunks: null } : { ...base, binary: false, hunks: diffLines(x, y) };
}

self.onmessage = async (ev: MessageEvent<ReplayRequest>) => {
  const m = ev.data;
  try {
    let value: unknown, transfer: Transferable[] = [];
    if (m.t !== 'load' && !t) throw new Error('no recording is open');
    switch (m.t) {
      case 'load': value = await load(m.data, m.sidecars ?? {}); break;
      case 'turn': value = await turn(m.p); break;
      case 'compare': value = await t!.compare(m.a, m.z); break;
      case 'file': value = view(await entry(m.p, m.path)); break;
      case 'diff': value = await diff(m.a, m.z, m.path); break;
      case 'branch': value = t!.fork(m.parent, m.at); break;
      case 'run': value = info((await t!.run(m.b, m.command)).call); break;
      case 'restore':
        for (const br of m.branches) t!.branches.push({ parent: br.parent, at: br.at, calls: br.calls.map(c => ({ ...c, snapshot: undefined })) });
        break;
      case 'fork': case 'export': {
        const zip = m.t === 'fork' ? await (await t!.stateAt(m.p.b, m.p.i)).save() : await t!.export(m.b, m.from);
        value = zip; transfer = [zip.buffer as ArrayBuffer];
        break;
      }
    }
    post({ id: m.id, ok: true, value }, transfer);
  } catch (e) {
    post({ id: m.id, ok: false, error: e instanceof Error ? e.message : String(e) });
  }
};
