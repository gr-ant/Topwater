// python3: CPython compiled to WebAssembly, run over this machine's files (wasi.ts). Opt-in: it exists only after the
// host calls Shell.allowPython. See docs/python.md.
import type { Shell } from '../shell.js';
import { DeadlineExceeded, specMessage, specOf, tooLarge, type CommandContext } from '../command.js';
import { concatBytes, decodeBytes, encodeText } from '../bytes.js';
import { ordinal } from '../vfs.js';
import { WebFailure, WebSession, WebUrl } from '../web.js';
import { COMMANDS } from '../spec.generated.js';
import * as wasi from '../wasi.js';

/** What Shell.allowPython takes. maxMemory: bytes of guest memory per call (the interpreter itself starts at 40 MB; a
 * program past the limit gets MemoryError). artifacts: the folder (path or URL) holding python.wasm.gz and
 * python314.zip; undefined: the topwater-python add-on's. */
export interface PythonOptions { maxMemory?: number; artifacts?: string | URL }

const DEFAULT_MAX_MEMORY = 256 << 20;
const NO_NETWORK = new WebSession({ explainBlocked: false }); // curl's: every host fails to resolve

/** Registers python3 when the host enabled it; registerAll calls this without options, which registers nothing. */
export function registerPython(shell: Shell, options?: PythonOptions) {
  if (options === undefined) return;
  const spec = specOf('python3');
  shell.register('python3', ctx => python3(ctx, options), spec.summary, spec.help ?? undefined);
}

/** The interpreter these options run: loaded and compiled on first use in a process. Rejects with PythonUnavailable. */
export const pythonRuntime = (options: PythonOptions) => wasi.runtime(options.artifacts, options.maxMemory ?? DEFAULT_MAX_MEMORY);

/** The HTTP bridge's sender: each request goes through the shell's web access (its policy, credentials, limits and
 * recording), one hop at a time; the guest follows redirects itself, each checked again. */
function bridge(ctx: CommandContext): wasi.HttpSender {
  const session = ctx.shell.web ?? NO_NETWORK;
  const failed = (method: string, url: string, reason: string) => encodeText(specMessage('python3', 'httpFailed', { method, url, reason }));
  return async (method, url, headers, body, timeoutMs) => {
    let r;
    try {
      r = await session.send(WebUrl.parse(url), method, headers, body.length || ['POST', 'PUT', 'PATCH'].includes(method) ? body : null, timeoutMs, ctx);
    } catch (e) {
      if (e instanceof DeadlineExceeded)
        return [wasi.HTTP_TIMEOUT, failed(method, url, specMessage('curl', 'timeout', { ms: String(Math.floor(timeoutMs)), bytes: '0' }))];
      if (!(e instanceof WebFailure)) throw e;
      // curl's wording for the same failure
      const key = e.blocked !== null ? 'blocked' : e.kind;
      const reason = key in (COMMANDS.get('curl')?.messages ?? {})
        ? specMessage('curl', key, { reason: e.blocked ?? '', host: e.host, port: String(e.port), ms: e.detail || '0', bytes: '0', max: e.detail, scheme: e.host, request: e.detail })
        : e.kind;
      return [e.kind === 'timeout' ? wasi.HTTP_TIMEOUT : wasi.HTTP_NETWORK, failed(method, url, reason)];
    }
    if (r.truncated) return [wasi.HTTP_NETWORK, failed(method, url, specMessage('curl', 'tooLarge'))];
    return [wasi.HTTP_OK, wasi.encodeResponse(r.status, r.headers, r.body)];
  };
}

/** Consecutive writes to one stream joined, so a multi-byte character split across writes decodes whole. */
function runs(output: [1 | 2, Uint8Array][]): [1 | 2, Uint8Array][] {
  const out: [1 | 2, Uint8Array[]][] = [];
  for (const [which, data] of output) {
    if (out.length && out[out.length - 1][0] === which) out[out.length - 1][1].push(data);
    else out.push([which, [data]]);
  }
  return out.map(([which, parts]) => [which, concatBytes(parts)]);
}

async function python3(ctx: CommandContext, options: PythonOptions): Promise<number> {
  const sh = ctx.shell, spec = specOf('python3'), args = ctx.args;
  if (['-h', '-?', '--help', '--help-all'].includes(args[0])) { ctx.stdout.write(spec.help ?? ''); return 0; } // the spec's help, in our words
  if (['-V', '--version'].includes(args[0])) { ctx.stdout.write(spec.version + '\n'); return 0; }
  const maxMemory = options.maxMemory ?? DEFAULT_MAX_MEMORY;
  let rt: wasi.Runtime;
  try { rt = await pythonRuntime(options); } catch (e) {
    if (!(e instanceof wasi.PythonUnavailable)) throw e;
    ctx.stderr.write(`python3: ${specMessage('python3', 'unavailable', { reason: e.message })}\n`);
    return 1;
  }
  const env: [string, string][] = [...sh.environment.keys()].filter(k => k !== 'PWD' && sh.isExported(k)).sort(ordinal)
    .map(k => [k, sh.environment.get(k)!] as [string, string]);
  env.push(['PWD', sh.cwd]);
  const deadline = Date.now() + ctx.remainingMs();
  const mounts = sh.mountTable;

  /** A mounted file or folder the program reached before it was fetched: fetch it now (this thread is free while the
   * guest waits in its worker), unlike other commands, which say "run the command again". */
  const fetch = async () => {
    if (![...mounts.items.values()].some(m => m.misses.size)) return false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const late = new Promise<false>(r => { timer = setTimeout(() => r(false), Math.min(Math.max(0, deadline - Date.now()) + 5000, 2 ** 31 - 1)); });
    try { return await Promise.race([mounts.after().then(() => true), late]); } finally { clearTimeout(timer); }
  };

  const guest = new wasi.Guest(ctx.fs, ['python3', ...args], env, encodeText(ctx.stdinText), sh.options.maxStringBytes, maxMemory,
    rt.stdlib, deadline, bridge(ctx), mounts.items.size ? fetch : undefined, sh.virtualNow !== undefined ? () => sh.now() : Date.now);
  let outcome: wasi.Outcome;
  try { outcome = await wasi.run(rt, guest); } catch (e) {
    if (e instanceof wasi.OutputFull) throw tooLarge('output', sh.options.maxStringBytes, 'write to a file in smaller pieces or filter earlier in the pipeline');
    throw e;
  }
  for (const [which, data] of runs(guest.output)) (which === 1 ? ctx.stdout : ctx.stderr).write(decodeBytes(data)); // in the order written, so 2>&1 interleaves them as a real process's
  for (const note of new Set(guest.notes)) ctx.stderr.write(`python3: ${note}\n`); // a file a mount had not fetched yet: the agent should run it again
  for (const [path, errno] of guest.writeBackErrors)
    ctx.stderr.write(`python3: ${specMessage('python3', 'writeBack', { file: path, errno: wasi.errnoText(errno) })}\n`);
  // past the call's (or `timeout`'s) limit, whatever the program saw: ends it as for any command. The guest stops at the
  // deadline's millisecond, the shell's check only after it: wait that out
  if (outcome.timedOut && Date.now() <= deadline) await new Promise(r => setTimeout(r, deadline - Date.now() + 1));
  ctx.checkTime();
  if (outcome.timedOut) {
    ctx.stderr.write(`python3: ${specMessage('python3', 'stopped')}\n`);
    return 1;
  }
  if (outcome.aborted) { ctx.stderr.write(`python3: ${specMessage('python3', 'aborted')}\n`); return 134; }
  if (outcome.stackOverflow) { ctx.stderr.write(`python3: ${specMessage('python3', 'stackOverflow')}\n`); return 1; }
  if (outcome.trap !== null) { ctx.stderr.write(`python3: ${specMessage('python3', 'fatal', { reason: outcome.trap })}\n`); return 1; }
  const code = outcome.code & 0xff;
  return code || (guest.writeBackErrors.length ? 1 : 0);
}
