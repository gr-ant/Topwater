// Messages between the recording viewer page (demo/replay.ts) and its worker (replay-worker.ts).
import type { Hunk } from './diff.js';

/** call `i` (-1: the start) of timeline `b` (0: the recording; others: branches typed in the viewer) */
export interface Point { b: number; i: number }

export type ReplayRequest =
  | { t: 'load'; id: number; data: Uint8Array; sidecars?: Record<string, Uint8Array> }
  | { t: 'turn'; id: number; p: Point }
  | { t: 'compare'; id: number; a: Point; z: Point }
  | { t: 'file'; id: number; p: Point; path: string }
  | { t: 'diff'; id: number; a: Point; z: Point; path: string }
  | { t: 'fork'; id: number; p: Point }
  /** a new branch after call `at` of `parent`: its id */
  | { t: 'branch'; id: number; parent: number; at: number }
  /** runs a command at the end of a branch: its CallInfo */
  | { t: 'run'; id: number; b: number; command: string }
  /** the branches again, after the page replaced a stuck worker (their machines are rebuilt by re-running) */
  | { t: 'restore'; id: number; branches: { parent: number; at: number; calls: CallInfo[] }[] }
  /** a branch as its own .topwater file */
  | { t: 'export'; id: number; b: number; from: string }
  /** the start of the body of web request `n` of call `p`: a WebBody */
  | { t: 'web'; id: number; p: Point; n: number };

export type ReplayReply = { id: number; ok: true; value: unknown } | { id: number; ok: false; error: string };

export interface CallInfo {
  index: number; command: string; stdout: string; stderr: string; exitCode: number; trailer?: string; elapsedMs: number; hasSnapshot: boolean;
  time?: number; files?: string[];
  /** the web requests the call made, in order (bodies come one at a time, as a WebBody) */
  web?: WebInfo[];
}

/** a recorded request and its answer: `error` for one that failed below the policy (no status then) */
export interface WebInfo { method: string; url: string; status: number; reason: string; error?: string; size: number }

/** the start of a response body as text (bytes that are no UTF-8 as U+FFFD); null when it holds a NUL, so is no text */
export interface WebBody { text: string | null; size: number; truncated: boolean }

export interface LoadedRecording {
  calls: CallInfo[];
  metadata: Record<string, string>;
  topwater: string;
  created: number;
  appCommands: string[];
  deterministic: boolean;
  user: string;
  hostname: string;
  startTime: number | null;
  randomSeed: number | null;
}

/** [path, isDirectory, mode, owner, mtime ms, size] */
export type TreeEntry = [string, boolean, number, string, number, number];

export interface Change { path: string; kind: 'added' | 'removed' | 'modified' | 'replaced' | 'permissions'; isDirectory: boolean; isLink?: boolean; detail?: string }

export interface TurnView {
  p: Point;
  tree: TreeEntry[];
  /** what the call changed (from the machine before it) */
  changes: Change[];
  cwd: string;
  /** where this machine came from: the start, the recording's snapshot after this call, re-running calls after `from`, or a branch typed here */
  source:
    | { kind: 'start' }
    | { kind: 'snapshot' }
    | { kind: 'typed' }
    | { kind: 'rerun'; from: number; matches: boolean; stdout?: string; stderr?: string; exitCode?: number };
}

export type FileView =
  | { kind: 'missing' }
  | { kind: 'directory' }
  | { kind: 'link'; target: string }
  | { kind: 'text'; text: string; truncated: boolean; size: number }
  | { kind: 'binary'; size: number; head: Uint8Array };

export interface DiffView { binary: boolean; before: FileView['kind']; after: FileView['kind']; beforeSize: number; afterSize: number; hunks: Hunk[] | null }
