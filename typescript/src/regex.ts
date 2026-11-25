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
  const groups: number[] = []; // where each open group starts in out
  let atom = 0; // where the last atom starts in out
  let quantified: string | null = null; // the quantifier on the last atom, as emitted
  let wraps = 0; // groups wrapped around the last atom
  const addAtom = (text: string) => { atom = out.length; quantified = null; wraps = 0; out += text; };
  const quantifier = (text: string) => {
    if (quantified !== null && quantified.length === 1 && text.length === 1) {
      // a run of * + ? is one quantifier: ++ is +, ?? is ?, any other mix is *; linear however long the run
      const merged = quantified === text ? text : '*';
      if (merged !== quantified) { out = out.slice(0, -1) + merged; quantified = merged; }
      return;
    }
    if (quantified !== null) {
      // JS rejects a{2}{3}; POSIX applies them in turn. Each wrap copies the pattern, so keep it to a few.
      if (++wraps > 32) throw new ShellError('Regular expression too big', 2);
      out = out.slice(0, atom) + '(?:' + out.slice(atom) + ')';
    }
    quantified = text;
    out += text;
  };
  for (let i = 0; i < n; i++) {
    let c = pattern[i];
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
      if (j >= n) { addAtom('\\['); last = '['; continue; }
      addAtom('[' + body + ']');
      last = ']';
      i = j;
      continue;
    }
    const escaped = c === '\\' && i + 1 < n;
    if (escaped) c = pattern[++i];
    const op = escaped !== extended && '(){}|+?'.includes(c); // operators: bare in ERE, escaped in BRE
    const leading = last === null || last === '^' || last === '(' || last === '|'; // a quantifier here is a literal
    let end: number | null = null;
    if (escaped && (c === '<' || c === '>')) addAtom('\\b');
    else if (op && c === '(') { groups.push(out.length); out += '('; }
    else if (op && c === ')' && groups.length > 0) { atom = groups.pop()!; quantified = null; wraps = 0; out += ')'; }
    else if (op && c === '|') out += '|';
    else if (((c === '*' && !escaped) || (op && (c === '+' || c === '?'))) && !leading) quantifier(c);
    else if (op && c === '{' && !leading && (end = intervalEnd(pattern, i + 1, extended)) !== null) {
      quantifier('{' + (pattern[i + 1] === ',' ? '0' : '') + pattern.slice(i + 1, end) + '}');
      i = end + (extended ? 0 : 1);
    } else if (escaped && !'(){}|+?*'.includes(c)) addAtom('\\' + c); // \. \w \1 ...
    else addAtom('(){}|+?*'.includes(c) ? '\\' + c : c === '/' ? '\\/' : c); // a literal, or . ^ $ as they are
    last = op && (c === '(' || c === '|') ? c : escaped || c === '(' || c === '|' ? 'x' : c;
  }
  return out;
}

/** The end of an interval whose text starts at `i` (after the "{"): the index of its "}" (of "\}" in basic mode), or
 * null when the text is not m, m, m,n or ,n. */
function intervalEnd(p: string, i: number, extended: boolean): number | null {
  let j = i;
  while (j < p.length && /[0-9,]/.test(p[j])) j++;
  const body = p.slice(i, j);
  if (body.length === 0 || body === ',' || body.split(',').length > 2) return null;
  if (extended) return p[j] === '}' ? j : null;
  return p[j] === '\\' && p[j + 1] === '}' ? j : null;
}

export const CLASS_SYNTAX = 'character class syntax is [[:space:]], not [:space:]';

/**
 * The error GNU grep/sed report for a malformed POSIX regex (basic unless extended), or null when it is valid.
 * grep is lenient where sed is not: in grep -E a stray ")" and a "{" that does not start a valid interval are literals.
 */
export function posixRegexError(p: string, extended: boolean, grep: boolean): string | null {
  const content = 'Invalid content of \\{\\}', unmatched = 'Unmatched [, [^, [:, [., or [=';
  const n = p.length;
  let open = 0, closed = 0;
  let atStart = true; // an interval here has nothing to repeat
  for (let i = 0; i < n;) {
    let c = p[i];
    if (c === '[') {
      let j = i + 1;
      if (j < n && p[j] === '^') j++;
      if (j >= n) return 'Invalid regular expression';
      const start = j;
      if (p[j] === ']') j++;
      let low: string | null = null; // the character a following '-' starts a range from
      while (j < n && p[j] !== ']') {
        if (p[j] === '[' && j + 1 < n && ':.='.includes(p[j + 1])) {
          const kind = p[j + 1];
          const close = p.indexOf(kind + ']', j + 2);
          if (close < 0) return unmatched;
          const name = p.slice(j + 2, close);
          if (kind === ':' && !Object.hasOwn(POSIX_CLASSES, name)) return 'Invalid character class name';
          if (kind === '.' && name.length !== 1) return 'Invalid collation character';
          low = kind === '.' ? name[0] : null;
          j = close + 2;
          continue;
        }
        if (p[j] === '-' && low !== null && j + 1 < n && p[j + 1] !== ']') {
          if (p[j + 1] < low) return 'Invalid range end';
          low = null;
          j += 2;
          continue;
        }
        low = p[j++];
      }
      if (j >= n) return unmatched;
      if (j - start >= 2 && p[start] === ':' && p[j - 1] === ':') return CLASS_SYNTAX;
      i = j + 1;
      atStart = false;
      continue;
    }
    const escaped = c === '\\';
    if (escaped) {
      if (i + 1 >= n) return 'Trailing backslash';
      c = p[++i];
    }
    i++;
    if (escaped && c >= '1' && c <= '9') {
      if (Number(c) > closed) return 'Invalid back reference';
    } else if (escaped !== extended && c === '(') { open++; atStart = true; continue; }
    else if (escaped !== extended && c === ')') {
      if (open === 0) { if (extended && grep) { atStart = false; continue; } return 'Unmatched ) or \\)'; }
      open--;
      closed++;
    } else if (escaped !== extended && c === '|') { atStart = true; continue; }
    else if (escaped !== extended && c === '{' && !atStart) {
      // {m} {m,} {m,n} {,n}; in grep -E anything else that is not obviously an interval is a literal "{"
      const lenient = extended && grep;
      const end = extended ? '}' : '\\}';
      let k = i;
      const num = (): number => {
        let value = -1;
        for (; k < n && p[k] !== ',' && !p.startsWith(end, k); k++)
          value = value === -2 || !(p[k] >= '0' && p[k] <= '9') ? -2 : Math.min(100000, Math.max(value, 0) * 10 + Number(p[k]));
        return value;
      };
      let lo = num(), hi: number;
      if (k >= n) { if (lenient) continue; return 'Unmatched \\{'; }
      if (lo === -2) { if (lenient) continue; return content; }
      if (p[k] === ',') {
        k++;
        hi = num();
        if (k >= n) { if (lenient) continue; return 'Unmatched \\{'; }
        if (hi === -2) { if (lenient) continue; return content; }
        if (p[k] === ',') return content;
        if (lo < 0) lo = 0;
      } else if (lo < 0) return content;
      else hi = lo;
      if (hi >= 0 && lo > hi) return content;
      if (Math.max(lo, hi) > 32767) return 'Regular expression too big';
      i = k + end.length;
    }
    atStart = false;
  }
  return open > 0 ? 'Unmatched ( or \\(' : null;
}

/** Compiles a POSIX regex, reporting a bad one as `ShellError(message, 2)`. */
export function compilePosix(pattern: string, extended: boolean, flags = '', bracketEscapes = false): RegExp {
  const src = posixToJs(pattern, extended, bracketEscapes);
  try { return new RegExp(src, flags); } catch (e) {
    throw new ShellError(`invalid regular expression: ${(e as Error).message.replace(/^Invalid regular expression: /, '')}`, 2);
  }
}
