// Messages between the page (session.ts) and the Web Worker that runs the shell (worker.ts).
import type { ShellOptions } from '../src/index.js';

/** Files, modes and shell variables: enough to start a fresh worker where the last finished call left off. */
export interface Snapshot {
  /** [path, 'd' | 'f', mode, owner, mtime, bytes (files only)], parents before children. */
  entries: [string, 'd' | 'f', number, string, number, Uint8Array?][];
  cwd: string;
  env: [string, string][];
  aliases: [string, string][];
  /** Function definitions as bash source (`name () { ...; }`). */
  functions: string[];
  history: string[];
}

export interface InitOptions extends Partial<Omit<ShellOptions, 'fileSystem'>> {}

export type ToWorker =
  | { t: 'init'; options: InitOptions; seed?: [path: string, content: string | Uint8Array][]; snapshot?: Snapshot;
      /** a whole machine in the snapshot zip format (docs/reference.md "Snapshots") to start from; `snapshot` then applies on top */
      snapshotZip?: Uint8Array }
  | { t: 'run'; id: number; command: string }
  | { t: 'put'; id: number; name: string; data: Uint8Array };

export type FromWorker =
  | { t: 'ready'; user: string; hostname: string; home: string; cwd: string }
  | { t: 'out'; id: number; text: string; stream: 'stdout' | 'stderr' }
  | { t: 'done'; id: number; stdout: string; stderr: string; exitCode: number; trailer?: string; cwd: string }
  | { t: 'put'; id: number; path?: string; error?: string }
  | { t: 'download'; name: string; data: Uint8Array }
  | { t: 'snapshot'; snapshot: Snapshot | null };
