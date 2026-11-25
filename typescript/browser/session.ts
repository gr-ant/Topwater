// The page's handle on a shell running in a Web Worker. A JavaScript call can't be interrupted from inside (one
// catastrophic regex, one long synchronous loop), so if a call outlives its time limit plus a grace period the page
// terminates the worker and starts a new one from the snapshot taken after the last finished call.
import type { FromWorker, InitOptions, Snapshot, ToWorker } from './protocol.js';

export interface RunResult {
  stdout: string;
  stderr: string;
  exitCode: number;
  /** A bracketed note when a limit ended the call, e.g. "[stopped: ...]". */
  trailer?: string;
  /** True when the page had to terminate the worker. */
  hardStopped?: boolean;
}

export interface SessionOptions {
  /** URL of the bundled worker script (worker.js). */
  workerUrl: string | URL;
  /** Shell options (limits, user, hostname); maxCallDurationMs also sets when the page steps in. */
  shell?: InitOptions;
  /** Files to create in a new session: [absolute path, text or bytes]. */
  seed?: [string, string | Uint8Array][];
  /** How long past maxCallDurationMs the page waits before terminating the worker. Default 2000 ms. */
  graceMs?: number;
  /** Called when a command in the shell runs `download FILE`. */
  onDownload?: (name: string, data: Uint8Array) => void;
}

interface Pending { resolve: (v: unknown) => void; onOutput?: (text: string, stream: 'stdout' | 'stderr') => void }

export class TopwaterSession {
  user = 'agent';
  hostname = 'topwater';
  home = '/home/agent';
  cwd = '/home/agent';
  private worker!: Worker;
  private snapshot: Snapshot | null = null;
  private nextId = 1;
  private readonly pending = new Map<number, Pending>();
  private queue: Promise<unknown> = Promise.resolve();

  constructor(private readonly options: SessionOptions) {}

  /** Starts the worker; resolves once the shell is ready. */
  start(): Promise<void> { return this.spawn(undefined); }

  /** Runs one command line. Calls run one at a time, in order. */
  run(command: string, onOutput?: (text: string, stream: 'stdout' | 'stderr') => void): Promise<RunResult> {
    return this.enqueue(() => this.runNow(command, onOutput));
  }

  /** Writes a file into the home directory as the agent (binary-safe); resolves to its path. */
  put(name: string, data: Uint8Array): Promise<string> {
    return this.enqueue(async () => {
      const id = this.nextId++;
      const reply = await this.request<{ path?: string; error?: string }>(id, { t: 'put', id, name, data });
      if (reply.error !== undefined) throw new Error(reply.error);
      return reply.path!;
    });
  }

  /** The worker is killed; the session ends. */
  dispose() { this.worker?.terminate(); }

  private enqueue<T>(fn: () => Promise<T>): Promise<T> {
    const p = this.queue.then(fn);
    this.queue = p.catch(() => undefined);
    return p;
  }

  private request<T>(id: number, msg: ToWorker, onOutput?: Pending['onOutput']): Promise<T> {
    return new Promise<T>(resolve => {
      this.pending.set(id, { resolve: resolve as (v: unknown) => void, onOutput });
      this.worker.postMessage(msg);
    });
  }

  private async runNow(command: string, onOutput?: Pending['onOutput']): Promise<RunResult> {
    const id = this.nextId++;
    const limit = this.options.shell?.maxCallDurationMs ?? 120_000;
    let streamed = '';
    let timer: ReturnType<typeof setTimeout> | undefined;
    const stopped = new Promise<RunResult>(resolve => {
      timer = setTimeout(() => resolve({ stdout: streamed, stderr: '', exitCode: 124, hardStopped: true, trailer: '' }), limit + (this.options.graceMs ?? 2000));
    });
    const done = this.request<RunResult>(id, { t: 'run', id, command }, (text, stream) => { if (stream === 'stdout') streamed += text; onOutput?.(text, stream); });
    const result = await Promise.race([done, stopped]);
    clearTimeout(timer);
    if (!result.hardStopped) return result;
    this.pending.delete(id);
    this.worker.terminate();
    const restored = this.snapshot !== null;
    await this.spawn(this.snapshot ?? undefined);
    result.trailer = restored
      ? `[stopped: this call ran past the ${Math.round(limit / 1000)} s time limit and could not be interrupted, so the page restarted the shell; files and variables are as they were before this call]`
      : '[stopped: this call ran past the time limit and could not be interrupted, so the page restarted the shell; the session was reset]';
    return result;
  }

  private spawn(snapshot: Snapshot | undefined): Promise<void> {
    this.worker = new Worker(this.options.workerUrl, { type: 'module' });
    this.snapshot = null;
    return new Promise(resolve => {
      this.worker.onmessage = (e: MessageEvent<FromWorker>) => {
        const m = e.data;
        switch (m.t) {
          case 'ready': ({ user: this.user, hostname: this.hostname, home: this.home, cwd: this.cwd } = m); resolve(); break;
          case 'snapshot': this.snapshot = m.snapshot; break;
          case 'download': this.options.onDownload?.(m.name, m.data); break;
          case 'out': this.pending.get(m.id)?.onOutput?.(m.text, m.stream); break;
          case 'done': this.cwd = m.cwd; this.settle(m.id, m); break;
          case 'put': this.settle(m.id, m); break;
        }
      };
      const init: ToWorker = snapshot ? { t: 'init', options: this.options.shell ?? {}, snapshot } : { t: 'init', options: this.options.shell ?? {}, seed: this.options.seed };
      this.worker.postMessage(init);
    });
  }

  private settle(id: number, value: unknown) {
    const p = this.pending.get(id);
    this.pending.delete(id);
    p?.resolve(value);
  }
}
