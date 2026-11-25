// Shell patterns: case, [[ == ]], ${x%pat}, and pathname expansion against the virtual filesystem.
import { VirtualFileSystem, ordinal } from './vfs.js';
import { POSIX_CLASSES } from './regex.js';

export const hasWildcard = (s: string) => /[*?[]|[@+!]\(/.test(s);

/** Escapes text so it matches itself as a pattern (quoted parts of a word). */
export function escapeGlob(s: string): string { return s.replace(/[*?[\]\\()|@!+]/g, '\\$&'); }

const escapeRe = (c: string) => c.replace(/[\\^$.*+?()[\]{}|/-]/g, '\\$&');

function hasUnescapedWildcard(seg: string): boolean {
  for (let i = 0; i < seg.length; i++) {
    if (seg[i] === '\\') { i++; continue; }
    if ('*?['.includes(seg[i]) || ('@+!'.includes(seg[i]) && seg[i + 1] === '(')) return true;
  }
  return false;
}
const unescape = (seg: string) => seg.replace(/\\(.)/g, '$1');

const cache = new Map<string, RegExp>();
/** A compiled-once regex: patterns repeat (a case in a loop), so building one per match would dominate. */
export function cachedRegex(source: string, flags = ''): RegExp {
  const key = flags + '/' + source;
  let re = cache.get(key);
  if (!re) {
    if (cache.size >= 1024) cache.clear();
    re = new RegExp(source, flags);
    cache.set(key, re);
  }
  return re;
}

/** A whole-string matcher. `slashes`: true for case/[[ ]]/${x%pat}, where * also matches '/'. */
export function globRegex(pattern: string, ignoreCase = false, slashes = false): RegExp {
  return cachedRegex('^(?:' + globBody(pattern, slashes) + ')$', ignoreCase ? 'is' : 's');
}

export function globMatch(pattern: string, text: string, ignoreCase = false): boolean {
  if (!hasWildcard(pattern) && !pattern.includes('\\')) return ignoreCase ? pattern.toLowerCase() === text.toLowerCase() : pattern === text;
  return globRegex(pattern, ignoreCase, true).test(text);
}

export function globBody(p: string, slashes = false): string {
  const state = { i: 0 };
  return body(p, state, slashes, false);
}

/** Translates the pattern from state.i, stopping at an unescaped | or ) inside an extglob group. */
function body(p: string, st: { i: number }, slashes: boolean, inGroup: boolean): string {
  const any = slashes ? '[^]' : '[^/]';
  let out = '';
  for (; st.i < p.length; st.i++) {
    const c = p[st.i];
    if (inGroup && (c === '|' || c === ')')) return out;
    if ('@!*+?'.includes(c) && p[st.i + 1] === '(' && groupEnd(p, st.i + 1) >= 0) {
      // extglob: @(a|b) one of, *(..) any number, +(..) at least one, ?(..) optional, !(..) anything else
      st.i += 2;
      const alts: string[] = [];
      for (;;) {
        alts.push(body(p, st, slashes, true));
        if (st.i >= p.length || p[st.i] === ')') break;
        st.i++; // past '|'
      }
      const group = '(?:' + alts.join('|') + ')';
      // shortcut: !(..) is exact only at the end of a pattern (rm !(*.txt)); make it a real complement if agents nest it
      out += c === '@' ? group : c === '*' ? group + '*' : c === '+' ? group + '+' : c === '?' ? group + '?' : `(?:(?!${group}$)${any}*)`;
      continue;
    }
    if (c === '\\' && st.i + 1 < p.length) { out += escapeRe(p[++st.i]); continue; }
    if (c === '*') { out += any + '*'; continue; }
    if (c === '?') { out += any; continue; }
    if (c === '[') {
      const close = bracketEnd(p, st.i);
      if (close > 0) { out += bracket(p.slice(st.i + 1, close)); st.i = close; continue; }
    }
    out += escapeRe(c);
  }
  return out;
}

function groupEnd(p: string, i: number): number {
  let depth = 0;
  for (; i < p.length; i++) {
    if (p[i] === '\\') { i++; continue; }
    if (p[i] === '(') depth++;
    else if (p[i] === ')' && --depth === 0) return i;
  }
  return -1;
}

/** The ']' closing the bracket expression that opens at i (allowing []...] and [[:class:]]), or -1. */
function bracketEnd(p: string, i: number): number {
  let k = i + 1;
  if (p[k] === '!' || p[k] === '^') k++;
  if (p[k] === ']') k++;
  for (; k < p.length; k++) {
    if (p[k] === '[' && ':.='.includes(p[k + 1] ?? '\0')) {
      const e = p.indexOf(p[k + 1] + ']', k + 2);
      if (e < 0) return -1;
      k = e + 1;
      continue;
    }
    if (p[k] === '\\') { k++; continue; }
    if (p[k] === ']') return k;
  }
  return -1;
}

function bracket(inner: string): string {
  let out = '[';
  let k = 0;
  if (inner[0] === '!' || inner[0] === '^') { out += '^'; k = 1; }
  for (; k < inner.length; k++) {
    let c = inner[k];
    if (c === '[' && inner[k + 1] === ':') {
      const e = inner.indexOf(':]', k + 2);
      if (e >= 0) { out += POSIX_CLASSES[inner.slice(k + 2, e)] ?? ''; k = e + 1; continue; }
    }
    if (c === '\\' && k + 1 < inner.length) c = inner[++k];
    if ('\\[]^/'.includes(c)) out += '\\';
    out += c;
  }
  return out + ']';
}

export interface GlobContext {
  fs: VirtualFileSystem;
  cwd: string;
  dotglob: boolean;
  globstar: boolean;
  nocaseglob: boolean;
}

const join = (display: string, name: string) => (display === '' ? name : display === '/' ? '/' + name : display + '/' + name);

/** Matches a pattern against the filesystem; paths come back in the pattern's form (relative or absolute), sorted. */
export function expandGlob(g: GlobContext, pattern: string): string[] {
  const absolute = pattern.startsWith('/');
  let current: [abs: string, display: string][] = [[absolute ? '/' : g.cwd, absolute ? '/' : '']];
  const segs = pattern.split('/').filter(Boolean);
  const fs = g.fs;
  for (let k = 0; k < segs.length; k++) {
    const seg = segs[k];
    const next: [string, string][] = [];
    if (seg === '**' && g.globstar) { // zero or more directories; as the last segment, everything below
      const last = !pattern.endsWith('/') && k === segs.length - 1;
      const queue = [...current];
      const bases = new Set(current.map(c => c[0] + '\0' + c[1]));
      while (queue.length) {
        const [abs, display] = queue.shift()!;
        const isBase = bases.has(abs + '\0' + display);
        if (!last) next.push([abs, display]);
        else if (display.length > 0 && isBase) next.push([abs, join(display, '')]);
        if (fs.isDirectory(abs) && fs.canRead(abs)) {
          for (const name of fs.list(abs)) {
            const child = VirtualFileSystem.normalize(abs, name);
            if (!g.dotglob && name.startsWith('.')) continue;
            if (fs.isDirectory(child, false)) queue.push([child, join(display, name)]); // like bash, ** does not follow links
            else if (last) next.push([child, join(display, name)]);
          }
          if (last && !isBase) next.push([abs, display]);
        }
      }
      current = next;
      continue;
    }
    for (const [abs, display] of current) {
      if (!fs.isDirectory(abs) || !fs.canRead(abs)) continue;
      if (seg === '.' || seg === '..') { next.push([VirtualFileSystem.normalize(abs, seg), join(display, seg)]); continue; }
      if (hasUnescapedWildcard(seg)) {
        const re = globRegex(seg, g.nocaseglob);
        for (const name of fs.list(abs))
          if (re.test(name) && (!name.startsWith('.') || seg.startsWith('.') || g.dotglob))
            next.push([VirtualFileSystem.normalize(abs, name), join(display, name)]);
      } else {
        const name = unescape(seg);
        const child = VirtualFileSystem.normalize(abs, name);
        if (fs.exists(child, false)) next.push([child, join(display, name)]);
      }
    }
    current = next;
  }
  if (pattern.endsWith('/'))
    return current.filter(c => c[1].length > 0 && fs.isDirectory(c[0])).map(c => (c[1] === '/' ? '/' : c[1] + '/')).sort(ordinal);
  return current.map(c => c[1]).filter(d => d.length > 0).sort(ordinal);
}
