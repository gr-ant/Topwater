// Web Worker entry: runs one Topwater shell off the page's thread. The page can terminate this worker at any moment
// (a call that overruns its time limit), so after every call it receives a snapshot to start a fresh worker from.
import { Shell, ShellError } from '../src/index.js';
import type { FromWorker, Snapshot, ToWorker } from './protocol.js';

/** Where the agent can write; everything else is the read-only system image every new Shell recreates. */
const SNAPSHOT_ROOTS = ['/home', '/tmp', '/var/tmp'];
const SNAPSHOT_MAX_BYTES = 32 << 20; // past this a snapshot per call gets slow; a hard stop then resets the session

let shell: Shell;
const post = (m: FromWorker, transfer: Transferable[] = []) => (self as unknown as Worker).postMessage(m, transfer);

function snapshot(): Snapshot | null {
  const fs = shell.fs;
  if (fs.usedBytes > SNAPSHOT_MAX_BYTES) return null;
  const entries: Snapshot['entries'] = [];
  const walk = (path: string) => {
    for (const name of fs.list(path)) {
      const p = `${path}/${name}`;
      const st = fs.stat(p);
      entries.push(st.isDirectory ? [p, 'd', st.mode, st.owner, st.modified] : [p, 'f', st.mode, st.owner, st.modified, fs.readBytes(p)]);
      if (st.isDirectory) walk(p);
    }
  };
  for (const root of SNAPSHOT_ROOTS) if (fs.isDirectory(root)) walk(root);
  return {
    entries, cwd: shell.cwd, env: [...shell.environment], aliases: [...shell.aliases],
    functions: [...shell.functions].map(([name, f]) => `${name} () \n${f.source}`), history: [...shell.history],
  };
}

async function restore(s: Snapshot) {
  const fs = shell.fs;
  for (const root of SNAPSHOT_ROOTS) if (fs.isDirectory(root)) for (const name of fs.list(root)) fs.delete(`${root}/${name}`, true);
  for (const [p, kind, mode, owner, , data] of s.entries) {
    if (kind === 'd') fs.mkdir(p, true); else fs.writeBytes(p, data!);
    fs.chmod(p, mode);
    fs.setOwner(p, owner);
  }
  for (const [p, , , , mtime] of [...s.entries].reverse()) fs.setModifiedTime(p, mtime); // children first: writing them touched their parents
  if (s.functions.length) await shell.run(s.functions.join('\n'));
  shell.environment = new Map(s.env);
  shell.aliases = new Map(s.aliases);
  try { shell.cwd = s.cwd; } catch { /* the directory is gone; stay home */ }
  shell.history.length = 0;
  shell.history.push(...s.history);
  shell.lastExitCode = 124; // the call that was stopped
}

/** A command only the demo has: hands a file to the page, which saves it in the browser. */
function registerDownload(sh: Shell) {
  sh.register('download', ctx => {
    if (ctx.args.length !== 1 || ctx.args[0].startsWith('-')) throw new ShellError('usage: download FILE   (saves FILE in your browser; archive a folder first: tar czf out.tgz DIR)', 2);
    const arg = ctx.args[0];
    const path = ctx.resolvePath(arg);
    if (ctx.fs.isDirectory(path)) throw new ShellError(`${arg}: Is a directory (archive it first: tar czf ${arg.replace(/\/+$/, '') || 'root'}.tgz ${arg})`);
    const data = ctx.fs.readBytes(path); // the agent's permissions apply
    const name = path.slice(path.lastIndexOf('/') + 1);
    post({ t: 'download', name, data }, [data.buffer as ArrayBuffer]);
    ctx.stdout.write(`download: sent ${name} (${data.length} bytes) to your browser\n`);
    return 0;
  }, 'Save a file from this shell in your browser (demo only)', 'download FILE');
}

self.onmessage = async (e: MessageEvent<ToWorker>) => {
  const m = e.data;
  switch (m.t) {
    case 'init': {
      shell = new Shell(m.options);
      registerDownload(shell);
      if (m.snapshot) await restore(m.snapshot);
      else {
        const agent = shell.fs.as(shell.options.user);
        for (const [p, content] of m.seed ?? []) {
          agent.mkdir(p.slice(0, p.lastIndexOf('/')) || '/', true);
          if (typeof content === 'string') agent.writeText(p, content); else agent.writeBytes(p, content);
        }
      }
      post({ t: 'ready', user: shell.options.user, hostname: shell.options.hostname, home: `/home/${shell.options.user}`, cwd: shell.cwd });
      post({ t: 'snapshot', snapshot: snapshot() });
      break;
    }
    case 'run': {
      // output is batched so a chatty loop doesn't flood the page with one message per write
      let pending: { text: string; stream: 'stdout' | 'stderr' } | null = null, last = Date.now();
      const flush = () => { if (pending) { post({ t: 'out', id: m.id, ...pending }); pending = null; } last = Date.now(); };
      const r = await shell.run(m.command, (text, stream) => {
        if (pending && pending.stream !== stream) flush();
        pending = pending ? { text: pending.text + text, stream } : { text, stream };
        if (Date.now() - last > 50 || pending.text.length > 64 << 10) flush();
      });
      flush();
      post({ t: 'done', id: m.id, stdout: r.stdout, stderr: r.stderr, exitCode: r.exitCode, trailer: r.trailer, cwd: shell.cwd });
      post({ t: 'snapshot', snapshot: snapshot() });
      break;
    }
    case 'put': {
      const name = m.name.replace(/^.*[\\/]/, '');
      const path = `/home/${shell.options.user}/${name}`;
      try {
        if (!name || name === '.' || name === '..') throw new ShellError(`${m.name}: not a valid file name`);
        shell.fs.as(shell.options.user).writeBytes(path, m.data); // as the agent: its permissions and the disk cap apply
        post({ t: 'put', id: m.id, path });
        post({ t: 'snapshot', snapshot: snapshot() });
      } catch (err) {
        post({ t: 'put', id: m.id, error: err instanceof Error ? err.message : String(err) });
      }
      break;
    }
  }
};
