// Runs one command handler directly, without the shell interpreter: for unit-testing commands in isolation.
import { VirtualFileSystem } from '../src/vfs.js';
import { StdinBuffer, type CommandContext, type CommandHandler, type Writer, DeadlineExceeded } from '../src/command.js';
import type { Shell } from '../src/shell.js';

export interface RunResult { stdout: string; stderr: string; exitCode: number }

/** A filesystem with /home/agent as the working directory, and a fake shell holding the registered commands. */
export function makeEnv() {
  const fs = new VirtualFileSystem();
  fs.mkdir('/home/agent', true);
  fs.mkdir('/tmp', true);
  const commands = new Map<string, CommandHandler>();
  const shell = {
    fs,
    cwd: '/home/agent',
    environment: new Map<string, string>([['HOME', '/home/agent'], ['USER', 'agent'], ['PATH', '/usr/bin:/bin']]),
    options: { maxStringBytes: 32 << 20, maxExtractBytes: 64 << 20, maxExtractEntries: 100_000, maxLoopIterations: 100_000 },
    register(name: string, handler: CommandHandler) { commands.set(name, handler); return shell; },
    resolvePath: (p: string) => VirtualFileSystem.normalize('/home/agent', p),
  };
  async function run(argv: string[], stdin = '', deadlineMs = 10_000, view: VirtualFileSystem = fs): Promise<RunResult> {
    let out = '', err = '';
    const stdout: Writer = { write: s => { out += s; } };
    const stderr: Writer = { write: s => { err += s; } };
    const deadline = { at: Date.now() + deadlineMs };
    const buf = new StdinBuffer(stdin);
    let stdinText: string | undefined;
    const handler = commands.get(argv[0]);
    if (!handler) throw new Error(`no command ${argv[0]}`);
    const ctx: CommandContext = {
      shell: shell as unknown as Shell,
      name: argv[0],
      args: argv.slice(1),
      stdout, stderr, fs: view,
      get stdinText() { return stdinText ??= buf.readAll(); },
      readLine: d => buf.readLine(d),
      resolvePath: shell.resolvePath,
      checkTime: () => { if (Date.now() > deadline.at) throw new DeadlineExceeded(deadline); },
      remainingMs: () => deadline.at - Date.now(),
      invoke: async () => { throw new Error('invoke is not available in the harness'); },
      stdin: buf,
    };
    let code: number;
    try { code = (await handler(ctx)) ?? 0; } catch (e) {
      if (e instanceof Error && 'exitCode' in e) { err += `${argv[0]}: ${e.message}\n`; code = (e as { exitCode: number }).exitCode; }
      else throw e;
    }
    return { stdout: out, stderr: err, exitCode: code };
  }
  return { fs, shell, run };
}
