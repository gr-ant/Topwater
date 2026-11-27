// Shell: the session state a snapshot stores, and putting it back. See docs/architecture.md.
import type { Shell } from '../shell.js';
import { ShellError } from '../vfs.js';
import { parse } from '../parser.js';
import { SNAPSHOT_SET_OPTIONS, type ShellStateData } from '../snapshot.js';
import { ShellArray, EXECUTABLE_STUB } from './types.js';

export class SessionPart {
  /** @internal The session as a snapshot stores it (see snapshot.ts). Call it between calls. */
  captureState(this: Shell): ShellStateData {
    this.epoch++; // arrays are now shared with the snapshot: copy each before its next write
    return {
      cwd: this.cwdPath, variables: new Map(this.environment), attributes: new Map(this.varAttrs), unexported: new Set(this.unexported),
      arrays: new Map([...this.arrays].map(([k, a]) => [k, { assoc: a.assoc, next: a.next, items: [...a.items] }])),
      functions: new Map([...this.functions].map(([k, f]) => [k, f.source])), exportedFunctions: new Set(this.exportedFunctions),
      aliases: new Map(this.aliases), traps: new Map(this.traps),
      setOptions: this.optionList.filter(([n, on]) => on && SNAPSHOT_SET_OPTIONS.has(n)).map(([n]) => n), ifsUnset: this.ifsUnset, shopts: [...this.shopts],
      positional: [...this.positional], dirStack: [...this.dirStack], history: [...this.history], lastExitCode: this.lastExitCode,
      secondsBase: this.secondsBase, lastArg: this.lastArg, pipeStatus: [...this.pipeStatus], getopts: [this.getoptsState.optind, this.getoptsState.pos],
      jobs: [...this.jobs], lastBackgroundPid: this.lastBg, nextPid: this.nextPid, clock: this.virtualClock, random: this.random,
      mounts: this.mountTable.capture(),
    };
  }

  /** @internal Puts a snapshot's session in place (copies, so the snapshot stays as it was) and lines /usr/bin up with this shell's commands. */
  applyState(this: Shell, s: ShellStateData) {
    this.epoch++;
    this.hashed = new Map(); // the restored machine's programs may be elsewhere
    this.environment = new Map(s.variables);
    this.varAttrs = new Map(s.attributes);
    this.unexported.clear(); for (const n of s.unexported) this.unexported.add(n);
    this.arrays = new Map([...s.arrays].map(([k, a]) => {
      const arr = new ShellArray(a.assoc);
      for (const [key, value] of a.items) arr.set(key, value);
      if (!a.assoc) arr.next = Math.max(arr.next, a.next);
      return [k, arr];
    }));
    this.functions = new Map([...s.functions].map(([name, body]) => {
      const c = parse(`${name}() ${body}\n`).items[0].first.commands[0];
      if (c.k !== 'func') throw new ShellError(`function ${name}: not a function definition`);
      return [name, { name, body: c.body, source: c.source }];
    }));
    this.exportedFunctions.clear(); for (const n of s.exportedFunctions) this.exportedFunctions.add(n);
    this.aliases = new Map(s.aliases);
    this.traps = new Map(s.traps);
    for (const o of SNAPSHOT_SET_OPTIONS) this.setOption(o, s.setOptions.includes(o));
    this.ifsUnset = s.ifsUnset;
    this.shopts = new Set(s.shopts);
    this.positional = [...s.positional];
    this.dirStack.length = 0; this.dirStack.push(...s.dirStack);
    this.history.length = 0; this.history.push(...s.history);
    this.lastExitCode = s.lastExitCode;
    this.secondsBase = s.secondsBase;
    this.lastArg = s.lastArg;
    this.pipeStatus = [...s.pipeStatus];
    this.getoptsState = { optind: s.getopts[0], pos: s.getopts[1] };
    this.jobs.clear(); for (const [pid, code] of s.jobs) this.jobs.set(pid, code);
    this.lastBg = s.lastBackgroundPid;
    this.nextPid = s.nextPid;
    // a deterministic shell picks up the time and random state where the snapshot left them; another keeps its own
    this.virtualNow = s.clock;
    this.randomState = s.random;
    this.variableChars = [...this.environment].reduce((n, [k, v]) => n + k.length + v.length, 0)
      + [...this.arrays.values()].reduce((n, a) => n + [...a.items].reduce((m, [k, v]) => m + k.length + v.length, 0), 0);
    this.cwdPath = this.fs.isDirectory(s.cwd) || this.mountTable.find(s.cwd) ? s.cwd : '/'; // a mount is listed at the next call
    // program files for exactly this shell's commands: the snapshot may come from a shell (or port) with others
    for (const dir of ['/usr/bin/', '/bin/'])
      if (this.fs.isDirectory(dir))
        for (const name of this.fs.list(dir))
          if (!this.commands.has(name) && this.fs.isFile(dir + name) && this.fs.readText(dir + name) === EXECUTABLE_STUB) this.fs.delete(dir + name);
    for (const name of this.commands.keys()) if (!this.fs.isFile('/usr/bin/' + name)) this.writeProgramFile(name, this.builtinNames?.has(name) === true);
  }
}
