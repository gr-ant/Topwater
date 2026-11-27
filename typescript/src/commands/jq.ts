// jq: a parser and evaluator for the jq language (see jq-parse.ts), with jq 1.7.1's builtins and output format.
// Every expression is a generator of {v, p} pairs: the value and, while a path expression is being evaluated
// (path(), =, |=, del()), the path that led to it; p is null when paths are not tracked and undefined for a
// value that is not a path (a literal in a path expression).

import { ShellError, LimitExceeded, DeadlineExceeded, type CommandContext } from '../command.js';
import type { Shell } from '../shell.js';
import { printable } from '../bytes.js';
import {
  JqError, JsonError, JsonReader, brief, cmp, dump, errType, isObj, parseJson, truthy, typeName, type J,
} from './jq-values.js';
import { CompileError, Parser, type Node, type Pat } from './jq-parse.js';
import {
  allPaths, arith, captureObject, contains, delPaths, flatten, format, fromEntries, fromIsoDate, getPath, gmtime, implode, index,
  indices, iterate, keys, length, makeRegex, matchObject, matches, mktime, numArg, setMaxBytes, setPath, sized, slice, sorted,
  splitString, strArg, strftime, strptime, toEntries, toNumber, toStr, toStream, trimWhite,
} from './jq-ops.js';

const MAX_STEPS = 20_000_000;

interface PV { v: J; p: J[] | null | undefined }
type Env = { parent: Env; key: string; val: any } | null;
interface Entry { params: string[]; body: Node; env: Env }
type Native = (I: Interp, a: Node[], inp: PV, env: Env) => Iterable<PV>;

class StepLimit extends Error {}
class Break extends Error { constructor(readonly id: object) { super('break'); } }
class Halt extends Error { constructor(readonly code: number, readonly text: string) { super('halt'); } }

const val = (inp: PV, v: J): PV => ({ v, p: inp.p === null ? null : undefined });
const untracked = (inp: PV): PV => inp.p === null ? inp : { v: inp.v, p: null };
/** Whether `v` (looking at a bounded number of nodes) may contain `target`. */
function mayReference(v: J, target: object, budget = { n: 200 }): boolean {
  if (v === target) return true;
  if (typeof v !== 'object' || v === null) return false;
  if (--budget.n < 0) return true;
  for (const c of Array.isArray(v) ? v : v.values()) if (mayReference(c, target, budget)) return true;
  return false;
}
const child = (x: PV, k: J, v: J): PV => ({ v, p: x.p ? [...x.p, k] : x.p });

function lookup(env: Env, key: string): Entry | undefined {
  for (let e = env; e; e = e.parent) if (e.key === key) return e.val;
  return preludeFuncs.get(key);
}
function lookupVar(env: Env, name: string): { found: boolean; v: J } {
  const key = '$' + name;
  for (let e = env; e; e = e.parent) if (e.key === key) return { found: true, v: e.val };
  return { found: false, v: null };
}

// ---------- the evaluator ----------

// nested calls of functions a filter defines; the same in every port, below any port's own stack limit
// shortcut: real jq recurses far deeper (and tail calls without limit); raise with an explicit stack per port if needed
const MAX_DEF_DEPTH = 100;

class Interp {
  steps = 0;
  depth = 0; // def calls in progress
  constructor(
    readonly ctx: CommandContext,
    readonly globals: Map<string, J>,
    readonly nextInput: () => J | undefined,
    readonly currentFile: () => string | null,
  ) {}

  step() {
    if (++this.steps > MAX_STEPS) throw new StepLimit();
    if ((this.steps & 4095) === 0) this.ctx.checkTime();
  }

  *cart(args: Node[], inp: PV, env: Env): Generator<J[]> {
    const go = function* (I: Interp, i: number, acc: J[]): Generator<J[]> {
      if (i === args.length) { yield acc; return; }
      for (const x of I.ev(args[i], untracked(inp), env)) yield* go(I, i + 1, [...acc, x.v]);
    };
    yield* go(this, 0, []);
  }

  *ev(n: Node, inp: PV, env: Env): Generator<PV> {
    this.step();
    switch (n.t) {
      case 'id': yield inp; return;
      case 'lit': yield val(inp, n.v); return;
      case 'pipe':
        for (const x of this.ev(n.l, inp, env)) yield* this.ev(n.r, x, env);
        return;
      case 'comma': yield* this.ev(n.l, inp, env); yield* this.ev(n.r, inp, env); return;
      case 'idx':
        for (const t of this.ev(n.target, inp, env))
          for (const k of this.ev(n.key, untracked(inp), env)) yield this.access(t, k.v, () => index(t.v, k.v));
        return;
      case 'slice':
        for (const t of this.ev(n.target, inp, env))
          for (const lo of n.lo ? this.ev(n.lo, untracked(inp), env) : [val(inp, null)])
            for (const hi of n.hi ? this.ev(n.hi, untracked(inp), env) : [val(inp, null)]) {
              const key = new Map<string, J>([['start', lo.v], ['end', hi.v]]);
              yield this.access(t, key, () => slice(t.v, lo.v, hi.v));
            }
        return;
      case 'iter':
        for (const t of this.ev(n.target, inp, env)) {
          if (t.p === undefined) throw this.badPath(t);
          if (isObj(t.v)) for (const [k, x] of [...t.v]) yield child(t, k, x);
          else { const items = iterate(t.v); for (let i = 0; i < items.length; i++) yield child(t, i, items[i]); }
        }
        return;
      case 'arr': {
        const out: J[] = [];
        if (n.body) for (const x of this.ev(n.body, untracked(inp), env)) { out.push(x.v); if (out.length > 1024) sized(8 * out.length); }
        yield val(inp, out);
        return;
      }
      case 'obj': yield* this.object(n.entries, 0, new Map(), inp, env); return;
      case 'str': for (const s of this.interpolate(n.parts, n.parts.length - 1, '', untracked(inp), env, n.fmt)) yield val(inp, s); return;
      case 'fmt': yield val(inp, format(n.name, inp.v)); return;
      case 'neg':
        for (const x of this.ev(n.x, untracked(inp), env)) {
          if (typeof x.v !== 'number') throw new JqError(`${errType(x.v)} cannot be negated`);
          yield val(inp, -x.v);
        }
        return;
      case 'bin':
        for (const r of this.ev(n.r, untracked(inp), env))
          for (const l of this.ev(n.l, untracked(inp), env)) {
            if ('+-*/%'.includes(n.op)) { yield val(inp, arith(n.op, l.v, r.v)); continue; }
            const c = cmp(l.v, r.v);
            // NaN is "less than" everything in jq's order but never equal
            yield val(inp, n.op === '==' ? c === 0 : n.op === '!=' ? c !== 0 : n.op === '<' ? c < 0 : n.op === '<=' ? c <= 0 : n.op === '>' ? c > 0 : c >= 0);
          }
        return;
      case 'and': case 'or': {
        const isOr = n.t === 'or';
        for (const a of this.ev(n.l, untracked(inp), env)) {
          if (truthy(a.v) === isOr) { yield val(inp, isOr); continue; }
          for (const b of this.ev(n.r, untracked(inp), env)) yield val(inp, truthy(b.v));
        }
        return;
      }
      case 'alt': {
        let found = false;
        const it = this.ev(n.l, inp, env)[Symbol.iterator]();
        for (;;) {
          let r: IteratorResult<PV>;
          try { r = it.next(); } catch (e) { if (e instanceof JqError) break; throw e; }
          if (r.done) break;
          if (truthy(r.value.v)) { found = true; yield r.value; }
        }
        if (!found) yield* this.ev(n.r, inp, env);
        return;
      }
      case 'if':
        for (const c of this.ev(n.c, untracked(inp), env)) {
          if (truthy(c.v)) yield* this.ev(n.a, inp, env);
          else if (n.b === null) yield inp;
          else yield* this.ev(n.b, inp, env);
        }
        return;
      case 'try': {
        const it = this.ev(n.body, inp, env)[Symbol.iterator]();
        for (;;) {
          let r: IteratorResult<PV>;
          try { r = it.next(); } catch (e) {
            if (e instanceof JqError) { if (n.handler) yield* this.ev(n.handler, val(inp, e.value), env); return; }
            throw e;
          }
          if (r.done) return;
          yield r.value;
        }
      }
      case 'var': {
        const r = lookupVar(env, n.name);
        if (r.found) yield val(inp, r.v);
        else if (n.name === 'ENV') yield val(inp, this.envObject());
        else if (this.globals.has(n.name)) yield val(inp, this.globals.get(n.name)!);
        else throw new JqError(`$${n.name} is not defined`);
        return;
      }
      case 'as':
        for (const x of this.ev(n.src, untracked(inp), env))
          for (const bound of this.bind(n.pat, x.v, env)) yield* this.ev(n.body, inp, bound);
        return;
      case 'reduce':
        for (const init of this.ev(n.init, inp, env)) {
          let acc = init;
          // `reduce ... (init; .[k] = v)` builds big objects: the accumulator this loop made is updated in place
          const fast = n.update.t === 'assign' && n.update.l.t === 'idx' && n.update.l.target.t === 'id' ? n.update : null;
          const owned = new Set<object>();
          for (const x of this.ev(n.src, untracked(inp), env))
            for (const bound of this.bind(n.pat, x.v, env)) {
              if (fast && acc.p === null) { this.step(); acc = this.fastAssign(fast, acc, bound, owned); continue; }
              let last: PV | undefined;
              for (const r of this.ev(n.update, acc, bound)) last = r;
              acc = last ?? val(inp, null);
            }
          yield acc;
        }
        return;
      case 'foreach':
        for (const init of this.ev(n.init, inp, env)) {
          let acc = init;
          for (const x of this.ev(n.src, untracked(inp), env))
            for (const bound of this.bind(n.pat, x.v, env))
              for (const next of this.ev(n.update, acc, bound)) {
                acc = next;
                if (n.extract) yield* this.ev(n.extract, acc, bound); else yield acc;
              }
        }
        return;
      case 'assign': yield* this.assign(n.op, n.l, n.r, inp, env); return;
      case 'call': yield* this.call(n.name, n.args, inp, env); return;
      case 'def': {
        const entry: Entry = { params: n.params, body: n.body, env: null };
        const e2: Env = { parent: env, key: `${n.name}/${n.params.length}`, val: entry };
        entry.env = e2;
        yield* this.ev(n.rest, inp, e2);
        return;
      }
      case 'label': {
        const id = {};
        try { yield* this.ev(n.body, inp, { parent: env, key: '*' + n.name, val: id }); } catch (e) { if (!(e instanceof Break && e.id === id)) throw e; }
        return;
      }
      case 'break': {
        for (let e = env; e; e = e.parent) if (e.key === '*' + n.name) throw new Break(e.val);
        throw new JqError('break');
      }
    }
  }

  private badPath(t: PV): JqError { return new JqError(`Invalid path expression with result ${brief(t.v)}`); }

  /** Extends a path by a key and reads the value; `read` does the access (and may throw). */
  private access(t: PV, key: J, read: () => J): PV {
    if (t.p === undefined) throw this.badPath(t);
    return { v: read(), p: t.p ? [...t.p, key] : t.p };
  }

  envObject(): J {
    const env = (this.ctx.shell as unknown as { environment?: Map<string, string> }).environment;
    return new Map<string, J>(env ?? []);
  }

  private *object(entries: [Node, Node][], i: number, acc: Map<string, J>, inp: PV, env: Env): Generator<PV> {
    if (i === entries.length) { yield val(inp, new Map(acc)); return; }
    const u = untracked(inp);
    for (const k of this.ev(entries[i][0], u, env)) {
      if (typeof k.v !== 'string') throw new JqError(`Object keys must be strings`);
      for (const x of this.ev(entries[i][1], u, env)) {
        const next = new Map(acc);
        next.set(k.v, x.v);
        yield* this.object(entries, i + 1, next, inp, env);
      }
    }
  }

  /** String interpolation, last \(...) varying slowest as in jq. */
  private *interpolate(parts: (string | Node)[], i: number, suffix: string, inp: PV, env: Env, fmt: string | null): Generator<string> {
    if (i < 0) { yield suffix; return; }
    const part = parts[i];
    if (typeof part === 'string') { yield* this.interpolate(parts, i - 1, part + suffix, inp, env, fmt); return; }
    for (const x of this.ev(part, inp, env)) {
      const s = fmt ? format(fmt, x.v) : toStr(x.v);
      sized(s.length + suffix.length);
      yield* this.interpolate(parts, i - 1, s + suffix, inp, env, fmt);
    }
  }

  private *bind(pat: Pat, value: J, env: Env): Generator<Env> {
    if (pat.t === 'var') { yield { parent: env, key: '$' + pat.name, val: value }; return; }
    if (pat.t === 'arr') {
      if (value !== null && !Array.isArray(value)) throw new JqError(`Cannot index ${typeName(value)} with number`);
      const go = function* (I: Interp, i: number, e: Env): Generator<Env> {
        if (i === pat.items.length) { yield e; return; }
        for (const e2 of I.bind(pat.items[i], index(value, i), e)) yield* go(I, i + 1, e2);
      };
      yield* go(this, 0, env);
      return;
    }
    const go = function* (I: Interp, i: number, e: Env): Generator<Env> {
      if (i === pat.entries.length) { yield e; return; }
      const [keyNode, sub] = pat.entries[i];
      for (const k of I.ev(keyNode, { v: value, p: null }, e)) {
        if (typeof k.v !== 'string') throw new JqError(`Cannot index ${typeName(value)} with ${typeName(k.v)}`);
        for (const e2 of I.bind(sub, index(value, k.v), e)) yield* go(I, i + 1, e2);
      }
    };
    yield* go(this, 0, env);
  }

  // -- assignment --

  private paths(lhs: Node, inp: PV, env: Env): J[][] {
    const out: J[][] = [];
    for (const x of this.ev(lhs, { v: inp.v, p: [] }, env)) {
      if (!x.p) throw this.badPath(x);
      out.push(x.p);
    }
    return out;
  }

  /** `paths |= update`: each path gets the first output of update; paths whose update is empty are deleted at the end. */
  private modify(lhs: Node, inp: PV, env: Env, update: (cur: J) => J | undefined): J {
    let out = inp.v;
    const owned = new Set<object>();
    const dead: J[] = [];
    for (const p of this.paths(lhs, inp, env)) {
      const r = update(getPath(out, p));
      if (r === undefined) dead.push(p); else out = setPath(out, p, 0, r, owned);
    }
    return dead.length ? delPaths(out, dead) : out;
  }

  private *assign(op: string, lhs: Node, rhs: Node, inp: PV, env: Env): Generator<PV> {
    if (op === '|=') {
      yield val(inp, this.modify(lhs, inp, env, cur => { for (const r of this.ev(rhs, { v: cur, p: null }, env)) return r.v; return undefined; }));
      return;
    }
    for (const x of this.ev(rhs, untracked(inp), env)) {
      if (op === '=') {
        let out = inp.v;
        const owned = new Set<object>();
        for (const p of this.paths(lhs, inp, env)) out = setPath(out, p, 0, x.v, owned);
        yield val(inp, out);
      } else if (op === '//=') yield val(inp, this.modify(lhs, inp, env, cur => truthy(cur) ? cur : x.v));
      else yield val(inp, this.modify(lhs, inp, env, cur => arith(op[0], cur, x.v)));
    }
  }

  /** One reduce step of the form `.[key] op= rhs`, changing the accumulator in place when `owned` holds it. */
  private fastAssign(u: Extract<Node, { t: 'assign' }>, acc: PV, env: Env, owned: Set<object>): PV {
    const keys = [...this.ev((u.l as Extract<Node, { t: 'idx' }>).key, untracked(acc), env)].map(k => k.v);
    if (u.op === '|=') {
      let out = acc.v;
      const dead: J[] = [];
      for (const k of keys) {
        let r: J | undefined;
        for (const y of this.ev(u.r, { v: index(out, k), p: null }, env)) { r = y.v; break; }
        if (r === undefined) dead.push([k]); else out = setPath(out, [k], 0, r, owned);
      }
      return { v: dead.length ? delPaths(out, dead) : out, p: null };
    }
    const vals = [...this.ev(u.r, untracked(acc), env)];
    if (vals.length === 0) return { v: null, p: null };
    let out = acc.v;
    for (const x of vals) {
      out = acc.v;
      // in place only for a single value that cannot contain the accumulator itself
      const own = vals.length === 1 && !mayReference(x.v, acc.v as object) ? owned : new Set<object>();
      for (const k of keys) {
        const cur = u.op === '=' ? null : index(out, k);
        const next = u.op === '=' ? x.v : u.op === '//=' ? (truthy(cur) ? cur : x.v) : arith(u.op[0], cur, x.v);
        out = setPath(out, [k], 0, next, own);
      }
    }
    return { v: out, p: null };
  }

  // -- calls --

  private *call(name: string, args: Node[], inp: PV, env: Env): Generator<PV> {
    const key = `${name}/${args.length}`;
    const entry = lookup(env, key);
    if (entry) { yield* this.callUser(entry, args, inp, env); return; }
    const native = natives.get(key);
    if (!native) throw new CompileError(`${key} is not defined`);
    yield* native(this, args, inp, env);
  }

  private *callUser(entry: Entry, args: Node[], inp: PV, env: Env): Generator<PV> {
    if (this.depth >= MAX_DEF_DEPTH) throw new JqError(`recursion too deep (more than ${MAX_DEF_DEPTH} nested function calls)`);
    let base = entry.env;
    const valueParams: [string, number][] = [];
    entry.params.forEach((p, i) => {
      if (p[0] === '$') valueParams.push([p.slice(1), i]);
      else base = { parent: base, key: `${p}/0`, val: { params: [], body: args[i], env } satisfies Entry };
    });
    // counts only while the body runs, not while it waits suspended after a yield (`f + f` keeps one waiting)
    const run = function* (I: Interp, e: Env): Generator<PV> {
      const it = I.ev(entry.body, inp, e);
      try {
        for (;;) {
          I.depth++;
          let r: IteratorResult<PV>;
          try { r = it.next(); } finally { I.depth--; }
          if (r.done) return;
          yield r.value;
        }
      } finally { it.return(undefined); }
    };
    if (valueParams.length === 0) { yield* run(this, base); return; }
    const go = function* (I: Interp, k: number, e: Env): Generator<PV> {
      if (k === valueParams.length) { yield* run(I, e); return; }
      const [pname, idx] = valueParams[k];
      for (const x of I.ev(args[idx], untracked(inp), env)) {
        const bound: Env = { parent: { parent: e, key: '$' + pname, val: x.v }, key: `${pname}/0`, val: { params: [], body: { t: 'lit', v: x.v }, env: null } satisfies Entry };
        yield* go(I, k + 1, bound);
      }
    };
    yield* go(this, 0, base);
  }
}

// ---------- native builtins ----------

const natives = new Map<string, Native>();
const reg = (name: string, arity: number, f: Native) => { natives.set(`${name}/${arity}`, f); };
/** A builtin that maps its input to one value. */
const fn0 = (name: string, f: (v: J, I: Interp) => J) => reg(name, 0, function* (I, _a, inp) { yield val(inp, f(inp.v, I)); });
/** A builtin of value arguments (their cartesian product) that returns one value. */
const fnN = (name: string, arity: number, f: (v: J, args: J[], I: Interp) => J) =>
  reg(name, arity, function* (I, a, inp, env) { for (const args of I.cart(a, inp, env)) yield val(inp, f(inp.v, args, I)); });
const genN = (name: string, arity: number, f: (v: J, args: J[], I: Interp) => Iterable<J>) =>
  reg(name, arity, function* (I, a, inp, env) { for (const args of I.cart(a, inp, env)) for (const r of f(inp.v, args, I)) yield val(inp, r); });

/**
 * Depth-first walk (recurse, while, until, repeat) over an explicit stack, so long chains use no recursion; a frame is
 * dropped before its last item is expanded, so a chain of single-output steps runs in constant space. Each node's
 * outputs are collected up front.
 */
type Item = { out: PV } | { kids: PV[] } | { node: PV };
const MAX_WALK_DEPTH = 200_000;
function* walk(root: PV, visit: (x: PV) => Item[]): Generator<PV> {
  const stack: { items: Item[]; i: number }[] = [{ items: [{ node: root }], i: 0 }];
  while (stack.length) {
    const frame = stack[stack.length - 1];
    const item = frame.items[frame.i++];
    if (frame.i >= frame.items.length) stack.pop();
    if ('out' in item) yield item.out;
    else {
      if (stack.length >= MAX_WALK_DEPTH) throw new JqError('recursion too deep');
      const items = 'node' in item ? visit(item.node) : item.kids.map(node => ({ node }));
      if (items.length) stack.push({ items, i: 0 });
    }
  }
}

const children = (x: PV): PV[] =>
  Array.isArray(x.v) ? x.v.map((e, i) => child(x, i, e)) : isObj(x.v) ? [...x.v].map(([k, e]) => child(x, k, e)) : [];

reg('empty', 0, function* () { /* nothing */ });
reg('error', 0, function* (_I, _a, inp) { throw new JqError(inp.v); });
reg('error', 1, function* (I, a, inp, env) { for (const m of I.ev(a[0], untracked(inp), env)) throw new JqError(m.v); });
reg('select', 1, function* (I, a, inp, env) { for (const c of I.ev(a[0], untracked(inp), env)) if (truthy(c.v)) yield inp; });
reg('path', 1, function* (I, a, inp, env) {
  for (const x of I.ev(a[0], { v: inp.v, p: [] }, env)) {
    if (!x.p) throw new JqError(`Invalid path expression with result ${brief(x.v)}`);
    yield val(inp, x.p);
  }
});
reg('getpath', 1, function* (I, a, inp, env) {
  for (const p of I.ev(a[0], untracked(inp), env)) {
    if (!Array.isArray(p.v)) throw new JqError('Path must be specified as an array');
    yield { v: getPath(inp.v, p.v), p: inp.p ? [...inp.p, ...p.v] : inp.p };
  }
});
reg('recurse', 0, function* (_I, _a, inp) { yield* walk(inp, x => [{ out: x }, { kids: children(x) }]); });
reg('recurse', 1, function* (I, a, inp, env) { yield* walk(inp, x => [{ out: x }, { kids: [...I.ev(a[0], x, env)] }]); });
reg('recurse', 2, function* (I, a, inp, env) {
  yield* walk(inp, x => [{ out: x }, { kids: [...I.ev(a[0], x, env)].filter(y => { for (const c of I.ev(a[1], untracked(y), env)) if (truthy(c.v)) return true; return false; }) }]);
});
reg('while', 2, function* (I, a, inp, env) {
  yield* walk(inp, x => [...I.ev(a[0], untracked(x), env)].flatMap((c): Item[] => truthy(c.v) ? [{ out: x }, { kids: [...I.ev(a[1], x, env)] }] : []));
});
reg('until', 2, function* (I, a, inp, env) {
  yield* walk(inp, x => [...I.ev(a[0], untracked(x), env)].map((c): Item => truthy(c.v) ? { out: x } : { kids: [...I.ev(a[1], x, env)] }));
});
reg('repeat', 1, function* (I, a, inp, env) { yield* walk(inp, x => [{ out: x }, { kids: [...I.ev(a[0], x, env)] }]); });
reg('range', 1, function* (I, a, inp, env) { yield* range(I, a, inp, env); });
reg('range', 2, function* (I, a, inp, env) { yield* range(I, a, inp, env); });
reg('range', 3, function* (I, a, inp, env) { yield* range(I, a, inp, env); });
function* range(I: Interp, a: Node[], inp: PV, env: Env): Generator<PV> {
  for (const b of I.cart(a, inp, env)) {
    if (b.some(x => typeof x !== 'number')) throw new JqError('Range bounds must be numeric');
    const [lo, hi, step] = (a.length === 1 ? [0, b[0], 1] : [b[0], b[1], b[2] ?? 1]) as number[];
    for (let k = 0; step > 0 ? lo + k * step < hi : step < 0 && lo + k * step > hi; k++) { I.step(); yield val(inp, lo + k * step); }
  }
}
reg('first', 1, function* (I, a, inp, env) { for (const x of I.ev(a[0], inp, env)) { yield x; return; } });
reg('last', 1, function* (I, a, inp, env) { let last: PV | undefined; for (const x of I.ev(a[0], inp, env)) last = x; yield last ?? val(inp, null); });
reg('limit', 2, function* (I, a, inp, env) {
  for (const n of I.ev(a[0], untracked(inp), env)) {
    if (typeof n.v !== 'number') throw new JqError('Invalid limit: must be a number');
    if (n.v <= 0) { if (n.v < 0) yield* I.ev(a[1], inp, env); continue; }
    let taken = 0;
    for (const x of I.ev(a[1], inp, env)) { yield x; if (++taken >= n.v) break; }
  }
});
reg('isempty', 1, function* (I, a, inp, env) { for (const _ of I.ev(a[0], untracked(inp), env)) { yield val(inp, false); return; } yield val(inp, true); });
reg('map', 1, function* (I, a, inp, env) {
  const out: J[] = [];
  for (const x of iterate(inp.v)) for (const y of I.ev(a[0], { v: x, p: null }, env)) out.push(y.v);
  yield val(inp, out);
});
reg('map_values', 1, function* (I, a, inp, env) {
  const first = (x: J): J | undefined => { for (const y of I.ev(a[0], { v: x, p: null }, env)) return y.v; return undefined; };
  if (isObj(inp.v)) {
    const r = new Map<string, J>();
    for (const [k, x] of inp.v) { const y = first(x); if (y !== undefined) r.set(k, y); }
    yield val(inp, r);
  } else yield val(inp, iterate(inp.v).flatMap(x => { const y = first(x); return y === undefined ? [] : [y]; }));
});
for (const [name, isAny] of [['any', true], ['all', false]] as const) {
  // any(gen; cond) / all(gen; cond) stop at the first deciding value
  const run = function* (I: Interp, gen: Node, cond: Node, inp: PV, env: Env): Generator<PV> {
    for (const g of I.ev(gen, untracked(inp), env))
      for (const c of I.ev(cond, untracked(g), env)) if (truthy(c.v) === isAny) { yield val(inp, isAny); return; }
    yield val(inp, !isAny);
  };
  const dot: Node = { t: 'id' }, iter: Node = { t: 'iter', target: dot };
  reg(name, 0, function* (I, _a, inp, env) { yield* run(I, iter, dot, inp, env); });
  reg(name, 1, function* (I, a, inp, env) { yield* run(I, iter, a[0], inp, env); });
  reg(name, 2, function* (I, a, inp, env) { yield* run(I, a[0], a[1], inp, env); });
}
reg('add', 1, function* (I, a, inp, env) { let acc: J = null; for (const x of I.ev(a[0], untracked(inp), env)) acc = arith('+', acc, x.v); yield val(inp, acc); });
reg('input', 0, function* (I, _a, inp) {
  const x = I.nextInput();
  if (x === undefined) throw new JqError('No more inputs');
  yield val(inp, x);
});
reg('inputs', 0, function* (I, _a, inp) { for (let x = I.nextInput(); x !== undefined; x = I.nextInput()) { I.step(); yield val(inp, x); } });
reg('debug', 0, function* (I, _a, inp) { I.ctx.stderr.write(`["DEBUG:",${dump(inp.v)}]\n`); yield inp; });
reg('stderr', 0, function* (I, _a, inp) { I.ctx.stderr.write(dump(inp.v)); yield inp; });
reg('halt', 0, function* () { throw new Halt(0, ''); });
reg('halt_error', 1, function* (I, a, inp, env) {
  for (const c of I.ev(a[0], untracked(inp), env)) {
    if (typeof c.v !== 'number') throw new JqError('halt_error/1: number required');
    throw new Halt(c.v, typeof inp.v === 'string' ? inp.v : dump(inp.v) + '\n');
  }
});
reg('tostream', 0, function* (_I, _a, inp) { for (const e of toStream(inp.v)) yield val(inp, e); });
reg('fromstream', 1, function* (I, a, inp, env) {
  let cur: J = null;
  for (const x of I.ev(a[0], untracked(inp), env)) {
    const e = x.v;
    if (!Array.isArray(e) || !Array.isArray(e[0])) throw new JqError('Invalid stream event');
    const path = e[0];
    if (e.length >= 2) {
      if (path.length === 0) { yield val(inp, e[1]); cur = null; continue; }
      cur = setPath(cur, path, 0, e[1], new Set());
    } else if (path.length === 1) { yield val(inp, cur); cur = null; }
  }
});
reg('input_filename', 0, function* (I, _a, inp) { yield val(inp, I.currentFile()); });
reg('env', 0, function* (I, _a, inp) { yield val(inp, I.envObject()); });
reg('path_exists', 1, function* (I, a, inp, env) { let ok = true; try { for (const _ of I.ev(a[0], { v: inp.v, p: [] }, env)) { /* just run it */ } } catch (e) { if (e instanceof JqError) ok = false; else throw e; } yield val(inp, ok); });

// sorting and grouping by a key expression
const keyed = (I: Interp, a: Node, v: J, env: Env): { k: J; x: J }[] => {
  if (!Array.isArray(v)) throw new JqError(`Cannot index ${typeName(v)} with number`);
  return v.map(x => ({ k: [...I.ev(a, { v: x, p: null }, env)].map(y => y.v), x })).sort((p, q) => cmp(p.k, q.k));
};
reg('sort_by', 1, function* (I, a, inp, env) { yield val(inp, keyed(I, a[0], inp.v, env).map(e => e.x)); });
reg('min_by', 1, function* (I, a, inp, env) { const l = keyed(I, a[0], inp.v, env); yield val(inp, l.length ? l[0].x : null); });
reg('max_by', 1, function* (I, a, inp, env) {
  const l = keyed(I, a[0], inp.v, env);
  yield val(inp, l.length ? l[l.length - 1].x : null);
});
const groups = (I: Interp, a: Node, v: J, env: Env) => {
  const out: { k: J; items: J[] }[] = [];
  for (const { k, x } of keyed(I, a, v, env)) {
    const last = out[out.length - 1];
    if (last && cmp(last.k, k) === 0) last.items.push(x); else out.push({ k, items: [x] });
  }
  return out;
};
reg('group_by', 1, function* (I, a, inp, env) { yield val(inp, groups(I, a[0], inp.v, env).map(g => g.items)); });
reg('unique_by', 1, function* (I, a, inp, env) { yield val(inp, groups(I, a[0], inp.v, env).map(g => g.items[0])); });

reg('with_entries', 1, function* (I, a, inp, env) {
  const out: J[] = [];
  for (const e of toEntries(inp.v)) for (const y of I.ev(a[0], { v: e, p: null }, env)) out.push(y.v);
  yield val(inp, fromEntries(out));
});
reg('sub', 2, function* (I, a, inp, env) { yield* substitute(I, a, inp, env, false); });
reg('sub', 3, function* (I, a, inp, env) { yield* substitute(I, a, inp, env, false); });
reg('gsub', 2, function* (I, a, inp, env) { yield* substitute(I, a, inp, env, true); });
reg('gsub', 3, function* (I, a, inp, env) { yield* substitute(I, a, inp, env, true); });
function* substitute(I: Interp, a: Node[], inp: PV, env: Env, all: boolean): Generator<PV> {
  const s = inp.v;
  if (typeof s !== 'string') throw new JqError(`${errType(s)} cannot be matched, as it is not a string`);
  for (const pattern of I.ev(a[0], untracked(inp), env))
    for (const flags of a.length > 2 ? I.ev(a[2], untracked(inp), env) : [val(inp, null)]) {
      const rx = makeRegex(pattern.v, flags.v);
      let results = [''];
      let pos = 0;
      for (const m of matches(rx, s, all || rx.global)) {
        const reps = [...I.ev(a[1], { v: captureObject(rx, m), p: null }, env)].map(r => {
          if (typeof r.v !== 'string') throw new JqError(`${errType(r.v)} cannot be added to a string`);
          return r.v;
        });
        const prefix = s.slice(pos, m.index);
        results = results.flatMap(acc => reps.map(r => { sized(acc.length + prefix.length + r.length); return acc + prefix + r; }));
        pos = m.index + m[0].length;
      }
      for (const acc of results) yield val(inp, acc + s.slice(pos));
    }
}

// value builtins
fn0('length', v => length(v));
fn0('utf8bytelength', v => new TextEncoder().encode(strArg('measured in UTF-8 bytes', v)).length);
fn0('not', v => !truthy(v));
fn0('keys', v => keys(v));
fn0('keys_unsorted', v => keys(v, false));
fn0('to_entries', v => toEntries(v));
fn0('from_entries', v => fromEntries(v));
fn0('add', v => {
  if (v === null) return null;
  const items = iterate(v);
  if (items.length > 0 && items.every(x => typeof x === 'string')) {
    let total = 0;
    for (const x of items as string[]) total += x.length;
    sized(total);
    return (items as string[]).join('');
  }
  return items.reduce<J>((acc, x) => arith('+', acc, x), null);
});
fn0('flatten', v => flatten(v, 1e9));
fnN('flatten', 1, (v, [d]) => flatten(v, numArg(d)));
for (const [name, f] of Object.entries({
  floor: Math.floor, ceil: Math.ceil, sqrt: Math.sqrt, fabs: Math.abs, trunc: Math.trunc, sin: Math.sin, cos: Math.cos, tan: Math.tan,
  asin: Math.asin, acos: Math.acos, atan: Math.atan, sinh: Math.sinh, cosh: Math.cosh, tanh: Math.tanh, asinh: Math.asinh,
  acosh: Math.acosh, atanh: Math.atanh, cbrt: Math.cbrt, exp: Math.exp, exp2: (x: number) => 2 ** x, exp10: (x: number) => 10 ** x,
  expm1: Math.expm1, log: Math.log, log2: Math.log2, log10: Math.log10, log1p: Math.log1p, rint: roundEven, nearbyint: roundEven,
  round: (x: number) => x < 0 ? -Math.round(-x) : Math.round(x), significand: (x: number) => x === 0 ? 0 : x / 2 ** Math.floor(Math.log2(Math.abs(x))),
  logb: (x: number) => Math.floor(Math.log2(Math.abs(x))),
})) fn0(name, v => f(numArg(v)));
function roundEven(x: number): number { const r = Math.round(x); return Math.abs(x % 1) === 0.5 && r % 2 !== 0 ? r - 1 : r; }
fn0('abs', v => typeof v === 'number' ? (v < 0 ? -v : v) : (() => { throw new JqError(`${errType(v)} has no absolute value`); })());
for (const [name, f] of Object.entries({
  pow: Math.pow, atan2: Math.atan2, fmin: Math.min, fmax: Math.max, fmod: (a: number, b: number) => a % b, ldexp: (a: number, b: number) => a * 2 ** b,
  scalb: (a: number, b: number) => a * 2 ** b, scalbln: (a: number, b: number) => a * 2 ** b, copysign: (a: number, b: number) => Math.sign(b) * Math.abs(a),
})) fnN(name, 2, (_v, [x, y]) => f(numArg(x), numArg(y)));
fn0('tostring', v => toStr(v));
fn0('tonumber', v => toNumber(v));
fn0('tojson', v => dump(v));
fn0('fromjson', v => parseJson(strArg('parsed as JSON', v)));
fn0('type', v => typeName(v));
fn0('infinite', () => Infinity);
fn0('nan', () => NaN);
fn0('isinfinite', v => { const n = numArg(v); return n === Infinity || n === -Infinity; });
fn0('isnan', v => Number.isNaN(numArg(v)));
fn0('isnormal', v => { const n = numArg(v); return Number.isFinite(n) && Math.abs(n) >= 2.2250738585072014e-308; });
fn0('sort', v => sorted(v));
fn0('unique', v => sorted(v).filter((x, i, l) => i === 0 || cmp(l[i - 1], x) !== 0));
fn0('min', v => { const l = sorted(v); return l.length ? l[0] : null; });
fn0('max', v => {
  const l = sorted(v);
  return l.length ? l[l.length - 1] : null;
});
fn0('reverse', v => {
  if (v === null) return [];
  if (Array.isArray(v)) return v.slice().reverse();
  if (typeof v === 'string') return Array.from(v).reverse().join('');
  throw new JqError(`Cannot reverse ${errType(v)}`);
});
fn0('ascii_downcase', v => strArg('lowercased', v).replace(/[A-Z]/g, c => c.toLowerCase()));
fn0('ascii_upcase', v => strArg('uppercased', v).replace(/[a-z]/g, c => c.toUpperCase()));
fn0('trim', v => trimWhite(strArg('trimmed', v), true, true));
fn0('ltrim', v => trimWhite(strArg('trimmed', v), true, false));
fn0('rtrim', v => trimWhite(strArg('trimmed', v), false, true));
fn0('explode', v => Array.from(strArg('exploded', v)).map(c => c.codePointAt(0)!));
fn0('implode', v => implode(v));
fn0('toarray', v => Array.isArray(v) ? v : [v]);
fn0('now', (_v, I) => I.ctx.shell.now() / 1000);
fn0('mktime', v => mktime(v));
fn0('gmtime', v => gmtime(numArg(v)));
fn0('localtime', v => gmtime(numArg(v)));
fn0('todate', v => strftime('%Y-%m-%dT%H:%M:%SZ', v));
fn0('todateiso8601', v => strftime('%Y-%m-%dT%H:%M:%SZ', v));
fn0('fromdate', v => fromIsoDate(v));
fn0('fromdateiso8601', v => fromIsoDate(v));
fn0('date', v => strftime('%Y-%m-%dT%H:%M:%SZ', v));
fn0('input_line_number', () => 0);
fn0('transpose', v => {
  const rows = iterate(v) as J[];
  const width = Math.max(0, ...rows.map(r => length(r)));
  return Array.from({ length: width }, (_, i) => rows.map(r => index(r, i)));
});
fn0('ascii', v => String.fromCharCode(numArg(v)));
fn0('have_literal_numbers', () => true);
fn0('have_decnum', () => false);
fnN('strftime', 1, (v, [f]) => strftime(strArg('formatted', f), v));
fnN('strflocaltime', 1, (v, [f]) => strftime(strArg('formatted', f), v));
fnN('strptime', 1, (v, [f]) => strptime(strArg('parsed as a date', v), strArg('used as a format', f)));
fnN('has', 1, (v, [k]) => {
  if (isObj(v) && typeof k === 'string') return v.has(k);
  if (Array.isArray(v) && typeof k === 'number') return k >= 0 && k < v.length;
  throw new JqError(`Cannot check whether ${typeName(v)} has a ${typeName(k)} key`);
});
fnN('contains', 1, (v, [x]) => contains(v, x));
fnN('inside', 1, (v, [x]) => contains(x, v));
fnN('startswith', 1, (v, [t]) => { if (typeof v !== 'string' || typeof t !== 'string') throw new JqError('startswith() requires string inputs'); return v.startsWith(t); });
fnN('endswith', 1, (v, [t]) => { if (typeof v !== 'string' || typeof t !== 'string') throw new JqError('endswith() requires string inputs'); return v.endsWith(t); });
fnN('ltrimstr', 1, (v, [t]) => typeof v === 'string' && typeof t === 'string' && t !== '' && v.startsWith(t) ? v.slice(t.length) : v);
fnN('rtrimstr', 1, (v, [t]) => typeof v === 'string' && typeof t === 'string' && t !== '' && v.endsWith(t) ? v.slice(0, v.length - t.length) : v);
fnN('trimstr', 1, (v, [t]) => {
  if (typeof v !== 'string' || typeof t !== 'string' || t === '') return v;
  let s: string = v;
  if (s.startsWith(t)) s = s.slice(t.length);
  return s.endsWith(t) ? s.slice(0, s.length - t.length) : s;
});
fnN('join', 1, (v, [sep]) => {
  const parts = iterate(v).map(x => {
    if (Array.isArray(x) || isObj(x)) throw new JqError(`Cannot join with ${typeName(x)}`);
    return x === null ? '' : toStr(x);
  });
  if (parts.length > 0 && typeof sep !== 'string') throw new JqError(`${errType(sep)} and string cannot be added`);
  const s = parts.join(typeof sep === 'string' ? sep : '');
  sized(s.length);
  return s;
});
fnN('split', 1, (v, [sep]) => {
  if (typeof v !== 'string' || typeof sep !== 'string') throw new JqError('split input and separator must be strings');
  return splitString(v, sep);
});
fnN('indices', 1, (v, [x]) => indices(v, x));
fnN('_indices', 1, (v, [x]) => indices(v, x));
fnN('index', 1, (v, [x]) => { const r = indices(v, x); return Array.isArray(r) && r.length ? r[0] : null; });
fnN('rindex', 1, (v, [x]) => { const r = indices(v, x); return Array.isArray(r) && r.length ? r[r.length - 1] : null; });
fnN('setpath', 2, (v, [p, x]) => { if (!Array.isArray(p)) throw new JqError('Path must be specified as an array'); return setPath(v, p, 0, x, new Set()); });
fnN('delpaths', 1, (v, [ps]) => delPaths(v, iterate(ps)));
fnN('bsearch', 1, (v, [x]) => {
  if (!Array.isArray(v)) throw new JqError(`${errType(v)} cannot be searched from`);
  let lo = 0, hi = v.length - 1;
  while (lo <= hi) { const mid = (lo + hi) >> 1; const c = cmp(v[mid], x); if (c === 0) return mid; if (c < 0) lo = mid + 1; else hi = mid - 1; }
  return -1 - lo;
});
reg('paths', 0, function* (_I, _a, inp) { for (const p of allPaths(inp.v)) yield val(inp, p); });
reg('leaf_paths', 0, function* (_I, _a, inp) { for (const p of allPaths(inp.v)) { const x = getPath(inp.v, p); if (!Array.isArray(x) && !isObj(x)) yield val(inp, p); } });

// regular expressions
genN('test', 1, (v, [re]) => regexIn(v, re, null, (rx, s) => [matches(rx, s, false).next().done === false]));
genN('test', 2, (v, [re, fl]) => regexIn(v, re, fl, (rx, s) => [matches(rx, s, false).next().done === false]));
genN('match', 1, (v, [re]) => regexIn(v, re, null, (rx, s) => [...matches(rx, s)].map(m => matchObject(rx, s, m))));
genN('match', 2, (v, [re, fl]) => regexIn(v, re, fl, (rx, s) => [...matches(rx, s)].map(m => matchObject(rx, s, m))));
genN('capture', 1, (v, [re]) => regexIn(v, re, null, (rx, s) => [...matches(rx, s)].map(m => captureObject(rx, m))));
genN('capture', 2, (v, [re, fl]) => regexIn(v, re, fl, (rx, s) => [...matches(rx, s)].map(m => captureObject(rx, m))));
const scan = (rx: ReturnType<typeof makeRegex>, s: string): J[] => [...matches(rx, s, true)].map(m => rx.names.length === 0 ? m[0] : m.slice(1).map(x => x ?? null));
genN('scan', 1, (v, [re]) => regexIn(v, re, null, scan));
genN('scan', 2, (v, [re, fl]) => regexIn(v, re, fl, scan));
const splitRe = (rx: ReturnType<typeof makeRegex>, s: string): J[] => {
  const pieces: J[] = [];
  let pos = 0;
  for (const m of matches(rx, s, true)) { pieces.push(s.slice(pos, m.index)); pos = m.index + m[0].length; }
  pieces.push(s.slice(pos));
  return [pieces];
};
genN('split', 2, (v, [re, fl]) => regexIn(v, re, fl, splitRe));
function regexIn(v: J, re: J, flags: J, f: (rx: ReturnType<typeof makeRegex>, s: string) => J[]): J[] {
  if (typeof v !== 'string') throw new JqError(`${errType(v)} cannot be matched, as it is not a string`);
  return f(makeRegex(re, flags), v);
}

// ---------- builtins written in jq ----------

const PRELUDE = `
def values: select(. != null);
def nulls: select(. == null);
def booleans: select(type == "boolean");
def numbers: select(type == "number");
def strings: select(type == "string");
def arrays: select(type == "array");
def objects: select(type == "object");
def iterables: select(type == "array" or type == "object");
def scalars: select(type != "array" and type != "object");
def finites: select(isinfinite or isnan | not);
def normals: select(isnormal);
def recurse_down: recurse;
def first: .[0];
def last: .[-1];
def nth($n): .[$n];
def nth($n; f): if $n < 0 then error("Out of bounds negative array index") else last(limit($n + 1; f)) end;
def in(xs): . as $x | xs | has($x);
def combinations: if length == 0 then [] else .[0][] as $x | (.[1:] | combinations) as $w | [$x] + $w end;
def combinations(n): . as $dot | [range(n)] | map($dot) | combinations;
def del(f): delpaths([path(f)]);
def paths(f): . as $dot | paths | select(. as $p | $dot | getpath($p) | f);
def pick(pathexps): . as $top | reduce path(pathexps) as $p (null; setpath($p; $top | getpath($p)));
def walk(f): def w: if type == "object" then map_values(w) elif type == "array" then map(w) else . end | f; w;
def debug(msg): (msg | debug | empty), .;
def splits($re; flags): split($re; flags) | .[];
def splits($re): splits($re; null);
def halt_error: halt_error(5);
def truncate_stream(stream): . as $n | null | stream | . as $input | if (.[0] | length) > $n then setpath([0]; .[0][$n:]) else empty end;
def INDEX(stream; idx_expr): reduce stream as $row ({}; .[$row | idx_expr | tostring] |= $row);
def INDEX(idx_expr): INDEX(.[]; idx_expr);
def IN(s): any(s == .; .);
def IN(src; s): any(src | IN(s); .);
.
`;

const preludeFuncs = new Map<string, Entry>();
const baseVars = new Set(['ENV', 'ARGS', '__prog_args']);
let preludeKeys: Set<string> | null = null;

function globalFuncs(): Set<string> {
  if (preludeKeys) return preludeKeys;
  const keys = new Set(natives.keys());
  const defs = new Parser(PRELUDE, keys, baseVars).prelude();
  for (const d of defs) { keys.add(d.key); preludeFuncs.set(d.key, { params: d.params, body: d.body, env: null }); }
  return preludeKeys = keys;
}

// ---------- the command ----------

const USAGE = `Usage:\tjq [OPTIONS] FILTER [FILES...]
\tjq [OPTIONS] --args FILTER [STRINGS...]
\tjq [OPTIONS] --jsonargs FILTER [JSON_TEXTS...]`;

const SHORT = 'rcnsSjReaMCtVhfe';
const LONG: Record<string, string> = {
  'raw-output': 'r', 'compact-output': 'c', 'null-input': 'n', slurp: 's', 'sort-keys': 'S', 'join-output': 'j', 'raw-input': 'R',
  'exit-status': 'e', 'ascii-output': 'a', 'monochrome-output': 'M', 'color-output': 'C', tab: 't', version: 'V', help: 'h', unbuffered: 'u', stream: 'T',
};

function run(ctx: CommandContext): number {
  const args = ctx.args;
  const flags = new Set<string>();
  const named = new Map<string, J>();
  const positional: J[] = [];
  let filter: string | null = null;
  let fromFile: string | null = null;
  const files: string[] = [];
  let indent = '  ';
  let rest: '--args' | '--jsonargs' | null = null;
  const fail = (msg: string, code = 2): never => { throw new ShellError(msg, code); };
  const readText = (f: string): string => {
    try { return printable(ctx.fs.readPipeText(ctx.resolvePath(f))); } catch (e) {
      if (e instanceof ShellError) throw new ShellError(`error: Could not open ${f}: ${e.message.slice(e.message.lastIndexOf(': ') + 2)}`, 2);
      throw e;
    }
  };

  for (let i = 0; i < args.length;) {
    const arg = args[i++];
    if (arg === '--arg' || arg === '--argjson' || arg === '--slurpfile' || arg === '--rawfile') {
      if (i + 1 >= args.length) fail(`${arg} takes two parameters (e.g. ${arg} varname value)`);
      const name = args[i], value = args[i + 1];
      i += 2;
      if (arg === '--arg') named.set(name, value);
      else if (arg === '--argjson') {
        try { named.set(name, parseJson(value)); } catch { fail(`Invalid JSON text passed to --argjson`); }
      } else {
        const text = readText(value);
        if (arg === '--rawfile') named.set(name, text);
        else {
          const r = new JsonReader(text), vals: J[] = [];
          try { for (let v = r.next(); v !== undefined; v = r.next()) vals.push(v); } catch { fail(`Bad JSON in --slurpfile ${name} ${value}`); }
          named.set(name, vals);
        }
      }
      continue;
    }
    if (arg === '--indent') {
      if (i >= args.length || !/^\d+$/.test(args[i])) fail('--indent takes a number');
      const n = Number(args[i++]);
      if (n > 7) fail('Cannot indent more than 7 characters');
      indent = ' '.repeat(n);
      continue;
    }
    if (arg === '-f' || arg === '--from-file') { if (i >= args.length) fail(`${arg} takes a parameter`); fromFile = args[i++]; continue; }
    if (arg === '--args' || arg === '--jsonargs') { rest = arg; continue; }
    if (arg === '--') { for (const a of args.slice(i)) positionalArg(a); break; }
    if (arg.startsWith('--') && arg.length > 2) {
      const f = LONG[arg.slice(2)];
      if (!f) fail(`Unknown option: ${arg}\n${USAGE}`);
      flags.add(f);
      continue;
    }
    if (arg.startsWith('-') && arg.length > 1) {
      for (const c of arg.slice(1)) {
        if (!SHORT.includes(c)) fail(`Unknown option: ${arg}\n${USAGE}`);
        flags.add(c);
      }
      continue;
    }
    positionalArg(arg);
  }
  function positionalArg(a: string) {
    if (filter === null && fromFile === null) filter = a;
    else if (rest === '--args') positional.push(a);
    else if (rest === '--jsonargs') { try { positional.push(parseJson(a)); } catch { fail('Invalid JSON text passed to --jsonargs'); } }
    else files.push(a);
  }

  if (flags.has('h')) { ctx.stdout.write(USAGE + '\n'); return 0; }
  if (flags.has('V')) { ctx.stdout.write('jq-1.7.1\n'); return 0; }
  if (fromFile !== null) { if (filter !== null) files.unshift(filter); filter = readText(fromFile); }
  if (filter === null) fail(USAGE);

  // compile
  const globals = new Map<string, J>(named);
  globals.set('ARGS', new Map<string, J>([['positional', positional], ['named', new Map(named)]]));
  globals.set('__prog_args', positional);
  let ast: Node;
  try {
    const vars = new Set([...baseVars, ...named.keys()]);
    ast = new Parser(filter!, globalFuncs(), vars).program();
  } catch (e) {
    if (e instanceof CompileError) fail(`error: ${e.message}\njq: 1 compile error`, 3);
    if (e instanceof RangeError) fail('error: filter is nested too deeply\njq: 1 compile error', 3);
    throw e;
  }

  const maxBytes = ctx.shell.options.maxStringBytes;
  setMaxBytes(maxBytes);
  let code = 0;
  let currentFile: string | null = null;

  // inputs, read lazily so `input`/`inputs` and early errors behave like jq's stream
  const sources = function* (): Generator<[string | null, string]> {
    if (files.length === 0) { yield [null, printable(ctx.stdinText)]; return; }
    for (const f of files) {
      if (f === '-') { yield [null, printable(ctx.stdinText)]; continue; }
      let text: string;
      try { text = printable(ctx.fs.readPipeText(ctx.resolvePath(f))); } catch (e) {
        if (!(e instanceof ShellError)) throw e;
        ctx.stderr.write(`jq: error: Could not open ${f}: ${e.message.slice(e.message.lastIndexOf(': ') + 2)}\n`);
        code = 2;
        continue;
      }
      yield [f, text];
    }
  };
  const documents = function* (): Generator<J> {
    const raw = flags.has('R'), slurp = flags.has('s');
    const all: J[] = [];
    for (const [file, text] of sources()) {
      currentFile = file;
      if (raw) {
        if (slurp) { all.push(text); continue; }
        const lines = text.split('\n');
        if (lines[lines.length - 1] === '') lines.pop();
        yield* lines;
        continue;
      }
      const reader = new JsonReader(text);
      for (;;) {
        let v: J | undefined;
        try { v = reader.next(); } catch (e) {
          if (!(e instanceof JsonError)) throw e;
          ctx.stderr.write(`jq: error (at ${file ?? '<stdin>'}:${reader.lineAt(e.pos)}): ${e.message} at ${reader.where(e.pos)}\n`);
          code = 2;
          return;
        }
        if (v === undefined) break;
        if (flags.has('T')) yield* [...toStream(v)]; else if (slurp) all.push(v); else yield v;
      }
    }
    if (slurp) yield raw ? all.join('') : all;
  };
  const stream = documents();
  const nextInput = () => { const r = stream.next(); return r.done ? undefined : r.value; };
  const interp = new Interp(ctx, globals, nextInput, () => currentFile);

  const compactUnit = flags.has('c') ? '' : flags.has('t') ? '\t' : indent;
  const rawOut = flags.has('r') || flags.has('j');
  let last: J = null;
  let any = false;
  const errorText = (e: JqError): string => {
    const v = e.value;
    return typeof v === 'string' ? `jq: error (at <unknown>): ${v}\n` : `jq: error (at <unknown>) (not a string): ${dump(v)}\n`;
  };

  try {
    for (let first = true; ; first = false) {
      let doc: J;
      if (flags.has('n')) { if (!first) break; doc = null; }
      else { const r = stream.next(); if (r.done) break; doc = r.value; }
      try {
        for (const out of interp.ev(ast, { v: doc, p: null }, null)) {
          last = out.v;
          any = true;
          const text = rawOut && typeof out.v === 'string' ? out.v : dump(out.v, compactUnit, flags.has('S'), flags.has('a'));
          ctx.stdout.write(flags.has('j') ? text : text + '\n');
        }
      } catch (e) {
        if (e instanceof JqError) { ctx.stderr.write(errorText(e)); code = 5; }
        else if (e instanceof Break) { ctx.stderr.write('jq: error (at <unknown>): break\n'); code = 5; }
        else if (e instanceof RangeError) { ctx.stderr.write('jq: error: recursion too deep\n'); code = 5; }
        else throw e;
      }
    }
  } catch (e) {
    if (e instanceof StepLimit) { ctx.stderr.write('jq: error (at <unknown>): too many steps (possible infinite loop)\n'); return 5; }
    if (e instanceof Halt) { if (e.text) ctx.stderr.write(e.text); return e.code; }
    if (e instanceof CompileError) fail(`error: ${e.message}\njq: 1 compile error`, 3);
    if (e instanceof LimitExceeded || e instanceof DeadlineExceeded) throw e;
    throw e;
  }
  if (code === 0 && flags.has('e')) return !any ? 4 : truthy(last) ? 0 : 1;
  return code;
}

export function registerJq(shell: Shell): void {
  shell.register('jq', run);
}

