import { ShellError } from './vfs.js';

/** POSIX bracket classes as the contents of a JS character class. */
export const POSIX_CLASSES: Record<string, string> = {
  alpha: 'a-zA-Z', digit: '0-9', alnum: 'a-zA-Z0-9', upper: 'A-Z', lower: 'a-z', space: ' \\t\\n\\r\\f\\v',
  blank: ' \\t', punct: '!-\\/:-@\\[-`{-~', xdigit: '0-9A-Fa-f', cntrl: '\\x00-\\x1f\\x7f', print: '\\x20-\\x7e',
  graph: '\\x21-\\x7e', word: 'a-zA-Z0-9_',
};

const escapeRe = (s: string) => s.replace(/[\\^$.*+?()[\]{}|/-]/g, '\\$&');
export { escapeRe as escapeRegex };

/**
 * POSIX regex (grep/sed/awk; basic unless `extended`) to a JS regex source: [[:class:]] brackets, \< \> word edges,
 * and in basic mode \( \) \{ \} \| \+ \? are the operators while bare ( ) { } | + ? are literals.
 * `bracketEscapes`: awk treats \/ \] \. inside brackets as escapes too.
 */
export function posixToJs(pattern: string, extended: boolean, bracketEscapes = false): string {
  let out = '';
  const n = pattern.length;
  let last: string | null = null; // last character emitted outside brackets, to spot a leading *
  for (let i = 0; i < n; i++) {
    const c = pattern[i];
    if (c === '[') {
      let j = i + 1;
      let body = '';
      if (j < n && pattern[j] === '^') { body += '^'; j++; }
      if (j < n && pattern[j] === ']') { body += '\\]'; j++; }
      while (j < n && pattern[j] !== ']') {
        if (pattern[j] === '[' && j + 1 < n && ':.='.includes(pattern[j + 1])) {
          const close = pattern.indexOf(pattern[j + 1] + ']', j + 2);
          if (close >= 0) {
            const name = pattern.slice(j + 2, close);
            if (pattern[j + 1] === ':') {
              const cls = POSIX_CLASSES[name];
              if (cls === undefined) throw new ShellError(`invalid character class '${name}'`, 2);
              body += cls;
            } else body += escapeRe(name);
            j = close + 2;
            continue;
          }
        }
        if (bracketEscapes && pattern[j] === '\\' && j + 1 < n) { body += pattern.slice(j, j + 2); j += 2; continue; }
        if ('\\[/'.includes(pattern[j])) body += '\\'; // backslash is literal in POSIX brackets
        body += pattern[j];
        j++;
      }
      if (j >= n) { out += '\\['; last = '['; continue; }
      out += '[' + body + ']';
      last = ']';
      i = j;
      continue;
    }
    if (c === '\\' && i + 1 < n) {
      const next = pattern[++i];
      if (next === '<' || next === '>') out += '\\b';
      else if (!extended && '(){}|+?'.includes(next)) out += next;
      else if (next === '/') out += '\\/';
      else out += '\\' + next;
      last = !extended && (next === '(' || next === '|') ? next : 'x';
      continue;
    }
    if (c === '*' && (last === null || last === '^' || last === '(' || last === '|')) out += '\\*'; // a leading * is literal
    else if (!extended && '(){}|+?'.includes(c)) out += '\\' + c;
    else if (c === '/') out += '\\/';
    else if (extended && c === '{' && !/^\{\d*(,\d*)?\}/.test(pattern.slice(i))) out += '\\{'; // a brace that is no interval is literal
    else out += c;
    last = !extended && '(|'.includes(c) ? 'x' : c;
  }
  return out;
}

/** Compiles a POSIX regex, reporting a bad one as `ShellError(message, 2)`. */
export function compilePosix(pattern: string, extended: boolean, flags = '', bracketEscapes = false): RegExp {
  const src = posixToJs(pattern, extended, bracketEscapes);
  try { return new RegExp(src, flags); } catch (e) {
    throw new ShellError(`invalid regular expression: ${(e as Error).message.replace(/^Invalid regular expression: /, '')}`, 2);
  }
}
