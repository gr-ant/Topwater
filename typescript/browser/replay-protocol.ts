// Messages between the recording viewer page (demo/replay.ts) and its worker (replay-worker.ts).
import type { Hunk } from './diff.js';

export type ReplayRequest =
  | { t: 'load'; id: number; data: Uint8Array; sidecars?: Record<string, Uint8Array> }
  | { t: 'turn'; id: number; index: number }
  | { t: 'file'; id: number; index: number; path: string }
  | { t: 'diff'; id: number; index: number; path: string }
  | { t: 'fork'; id: number; index: number };

export type ReplayReply = { id: number; ok: true; value: unknown } | { id: number; ok: false; error: string };

export interface CallInfo { index: number; command: string; stdout: string; stderr: string; exitCode: number; trailer?: string; elapsedMs: number; hasSnapshot: boolean }

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

export interface Change { path: string; kind: 'added' | 'removed' | 'modified' | 'replaced' | 'permissions'; isDirectory: boolean; detail?: string }

export interface TurnView {
  index: number;
  tree: TreeEntry[];
  changes: Change[];
  cwd: string;
  /** where this machine came from: the start, the recording's snapshot after this call, or re-running calls after `from` */
  source:
    | { kind: 'start' }
    | { kind: 'snapshot' }
    | { kind: 'rerun'; from: number; matches: boolean; stdout?: string; stderr?: string; exitCode?: number };
}

export type FileView =
  | { kind: 'missing' }
  | { kind: 'directory' }
  | { kind: 'text'; text: string; truncated: boolean; size: number }
  | { kind: 'binary'; size: number; head: Uint8Array };

export interface DiffView { binary: boolean; before: FileView['kind']; after: FileView['kind']; hunks: Hunk[] | null }
