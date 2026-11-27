// Shell: redirections, file descriptors and the output writers. See docs/architecture.md.
import type { Shell } from '../shell.js';
import { VirtualFileSystem, ShellError } from '../vfs.js';
import { StdinBuffer, tooLarge, nullWriter, type Writer } from '../command.js';
import type { Redirect } from '../parser.js';
import { DIAGNOSTICS, ERRNO, diagnostic } from '../spec.generated.js';
import type { Io } from './types.js';

// ---------- writers ----------

/** Buffers output (a pipeline stage, $(...), the call's stdout); past the string limit the call stops. */
export class BufferWriter implements Writer {
  protected chunks: string[] = [];
  length = 0;
  constructor(protected readonly limit: number) {}
  protected full(): void { throw tooLarge('output', this.limit, 'write to a file in smaller pieces or filter earlier in the pipeline'); }
  write(s: string) {
    if (!s) return;
    if (this.length + s.length > this.limit) this.full();
    this.chunks.push(s);
    this.length += s.length;
  }
  toString() {
    if (this.chunks.length > 1) this.chunks = [this.chunks.join('')];
    return this.chunks[0] ?? '';
  }
  clear() { this.chunks = []; this.length = 0; }
}

/** A redirect's file: buffered, but written through every megabyte, so the disk cap (not the string limit) bounds it. */
export class FileSink extends BufferWriter {
  constructor(private readonly fs: VirtualFileSystem, readonly path: string) { super(1 << 20); }
  protected override full() { this.flush(); }
  flush() {
    const text = this.toString();
    this.clear(); // dropped even if the disk is full, so a loop writing to a full disk doesn't grow the buffer
    if (text) this.fs.appendText(this.path, text);
  }
  override write(s: string) {
    if (s.length < 1 << 20) { super.write(s); return; }
    this.flush(); // a big write goes straight to the file instead of through the buffer
    this.fs.appendText(this.path, s);
  }
}

/** A file opened by `exec >file`: written through, so later commands in the same call can read it. */
export class FileAppender implements Writer {
  constructor(private readonly fs: VirtualFileSystem, private readonly path: string) {}
  write(s: string) { if (s) this.fs.appendText(this.path, s); }
}

/** A shell's stdout or stderr as `exec` sees it: what it points at can be changed for the rest of the shell. */
export class SwitchWriter implements Writer {
  constructor(public target: Writer) {}
  write(s: string) { this.target.write(s); }
}

// ---------- redirections ----------

export class RedirectionPart {
  /** @internal */ async applyRedirects(this: Shell, redirects: Redirect[], io: Io, sinks: FileSink[]): Promise<Io | null> {
    for (const r of redirects) {
      try {
        if (r.op === '<<' || r.op === '<<<' || r.op === '<') {
          const input = await this.openInput(r, io);
          io = r.fd >= 3 ? { ...io, fds: new Map(io.fds ?? []).set(r.fd, input) } : r.fd === 0 ? { ...io, stdin: input } : io;
        } else if (r.op === '>&' || r.op === '<&') {
          const target = await this.expandSingle(r.target!, io);
          if (target !== '-' && !/^\d+$/.test(target) && r.op === '>&') { // >&file is &>file
            const w = await this.openOutput({ ...r, op: '&>' }, io, path => { const s = new FileSink(this.agentFs, path); sinks.push(s); return s; });
            io = { ...io, out: w, err: w };
            continue;
          }
          let dup: Writer | StdinBuffer;
          if (target === '-') dup = r.op === '<&' ? new StdinBuffer('') : nullWriter;
          else {
            const d = /^\d+$/.test(target) ? this.descriptor(Number(target), io) : undefined;
            if (!d) throw new ShellError(diagnostic('redirectFailed', { target: target, reason: /^\d+$/.test(target) ? ERRNO.EBADF : DIAGNOSTICS.ambiguousRedirect }));
            dup = d;
          }
          io = withFd(io, r.fd, dup);
        } else {
          const w = await this.openOutput(r, io, path => { const s = new FileSink(this.agentFs, path); sinks.push(s); return s; });
          io = r.op.startsWith('&') ? { ...io, out: w, err: w } : withFd(io, r.fd, w);
        }
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        io.err.write(`${this.errPrefix}${e.message}\n`);
        return null;
      }
    }
    return io;
  }

  /** @internal */ async openInput(this: Shell, r: Redirect, io: Io): Promise<StdinBuffer> {
    if (r.op === '<<') return new StdinBuffer(await this.expandSingle(r.here!.body, io));
    if (r.op === '<<<') return new StdinBuffer((await this.expandSingle(r.target!, io)) + '\n');
    const target = await this.expandSingle(r.target!, io);
    const path = this.resolvePath(target);
    if (path === '/dev/stdin') return io.stdin;
    try { return new StdinBuffer(this.agentFs.readPipeText(path)); } catch (e) {
      if (e instanceof ShellError) throw new ShellError(`${target}: ${e.message.slice(e.message.lastIndexOf(': ') + 2)}`);
      throw e;
    }
  }

  /** @internal The writer for > >> >| &> &>>: /dev/null, /dev/stdout, /dev/stderr, or a file made by `open`. */
  async openOutput(this: Shell, r: Redirect, io: Io, open: (path: string) => Writer): Promise<Writer> {
    const target = await this.expandSingle(r.target!, io);
    const path = this.resolvePath(target);
    if (path === '/dev/null') return nullWriter;
    if (path === '/dev/stderr') return io.err;
    if (path === '/dev/stdout') return io.out;
    if (path.startsWith('/dev/fd/') && /^\d+$/.test(path.slice(8))) {
      const d = this.descriptor(Number(path.slice(8)), io);
      if (d && !(d instanceof StdinBuffer)) return d;
    }
    if (target === '') throw new ShellError(': No such file or directory');
    if (this.agentFs.isDirectory(path)) throw new ShellError(`${target}: Is a directory`);
    if (this.noclobber && (r.op === '>' || r.op === '&>') && this.agentFs.isFile(path)) throw new ShellError(diagnostic('noclobber', { target: target }));
    try {
      if (!r.op.endsWith('>>') || !this.agentFs.exists(path)) this.agentFs.writeText(path, ''); // truncate on open, like bash
    } catch (e) {
      if (e instanceof ShellError) throw new ShellError(`${target}: ${e.message.slice(e.message.lastIndexOf(': ') + 2)}`);
      throw e;
    }
    return open(path);
  }

  /** @internal What fd n refers to: 0 a StdinBuffer, 1 and 2 writers, 3+ whatever `exec` or this command opened. */
  descriptor(this: Shell, n: number, io: Io): Writer | StdinBuffer | undefined {
    if (n === 0) return io.stdin;
    if (n === 1) return io.out;
    if (n === 2) return io.err;
    return io.fds?.get(n) ?? this.fds.get(n);
  }

  /** @internal
   * `exec` with only redirections: they last for the rest of the shell. fds 1 and 2 are retargeted through the
   * SwitchWriter each (sub)shell starts with; files opened this way are written through as text arrives.
   */
  async execRedirects(this: Shell, redirects: Redirect[], io: Io): Promise<number> {
    for (const r of redirects) {
      try {
        let target: Writer | StdinBuffer | null;
        if (r.op === '<<' || r.op === '<<<' || r.op === '<') target = await this.openInput(r, io);
        else if (r.op === '>&' || r.op === '<&') {
          const t = await this.expandSingle(r.target!, io);
          if (t === '-') target = null;
          else {
            const d = /^\d+$/.test(t) ? this.descriptor(Number(t), io) : undefined;
            if (!d) throw new ShellError(diagnostic('redirectFailed', { target: t, reason: ERRNO.EBADF }));
            target = d instanceof SwitchWriter ? d.target : d;
          }
        } else target = await this.openOutput(r, io, path => new FileAppender(this.agentFs, path));
        let named = r.fd;
        if (r.fdVar !== undefined) {
          if (target === null) named = /^\d+$/.test(this.getVariable(r.fdVar)) ? Number(this.getVariable(r.fdVar)) : -1; // {fd}>&- closes it
          else { named = 10; while (this.fds.has(named)) named++; this.setVar(r.fdVar, String(named)); }
        }
        for (const fd of r.op.startsWith('&') ? [1, 2] : [named]) {
          if (fd < 0) continue;
          if (fd === 0) io.stdin.reset(target instanceof StdinBuffer ? target.readAll() : '');
          else if (fd <= 2) {
            const w = fd === 1 ? io.out : io.err;
            if (w instanceof SwitchWriter) w.target = target && !(target instanceof StdinBuffer) ? target : nullWriter;
          } else if (target === null) this.fds.delete(fd);
          else this.fds.set(fd, target);
        }
      } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        io.err.write(`${this.errPrefix}${e.message}\n`);
        return 1;
      }
    }
    return 0;
  }

  /** @internal `read -u N`: the input opened on fd N by `exec N<file`, or undefined. */
  inputFd(this: Shell, fd: number): StdinBuffer | undefined { const f = this.fds.get(fd); return f instanceof StdinBuffer ? f : undefined; }
}

// ---------- helpers ----------

export function withFd(io: Io, fd: number, target: Writer | StdinBuffer): Io {
  if (fd === 0) return target instanceof StdinBuffer ? { ...io, stdin: target } : io;
  if (fd === 1) return target instanceof StdinBuffer ? io : { ...io, out: target };
  if (fd === 2) return target instanceof StdinBuffer ? io : { ...io, err: target };
  return { ...io, fds: new Map(io.fds ?? []).set(fd, target) };
}
