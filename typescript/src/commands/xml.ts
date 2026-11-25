// A small, safe XML reader for Office documents: namespaces resolved, entities decoded, no DTDs (so no entity
// expansion tricks), no recursion while parsing, and a depth cap.

export class XmlError extends Error {}
/** The document declares a DTD, which is refused outright. */
export class DtdError extends XmlError {}

export interface XEl {
  /** Namespace URI ('' for none) and local name. */
  ns: string;
  local: string;
  /** Attributes by `local` (no namespace) or `{uri}local`. */
  attrs: Map<string, string>;
  kids: (XEl | string)[];
}

const MAX_DEPTH = 2000;
const NAME = /[^\s/>=]+/y;

const ENTITIES: Record<string, string> = { lt: '<', gt: '>', amp: '&', quot: '"', apos: "'" };
function decode(s: string): string {
  if (!s.includes('&')) return s;
  return s.replace(/&(#x[0-9a-fA-F]+|#[0-9]+|[A-Za-z][A-Za-z0-9]*);/g, (_, e: string) => {
    if (e[0] === '#') {
      const code = e[1] === 'x' ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10);
      if (!(code >= 0 && code <= 0x10ffff) || (code >= 0xd800 && code <= 0xdfff)) throw new XmlError('invalid character reference');
      return String.fromCodePoint(code);
    }
    if (e in ENTITIES) return ENTITIES[e];
    throw new XmlError(`undeclared entity '${e}'`);
  });
}

export const attrKey = (ns: string, local: string) => ns ? `{${ns}}${local}` : local;

export function parseXml(text: string): XEl {
  if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
  let pos = 0;
  const stack: { el: XEl; scope: Map<string, string>; name: string }[] = [];
  let root: XEl | null = null;
  const XML_NS = 'http://www.w3.org/XML/1998/namespace';
  const baseScope = new Map<string, string>([['xml', XML_NS]]);

  while (pos < text.length) {
    if (text[pos] !== '<') {
      const end = text.indexOf('<', pos);
      const raw = text.slice(pos, end < 0 ? text.length : end);
      pos = end < 0 ? text.length : end;
      const top = stack[stack.length - 1];
      if (top) top.el.kids.push(decode(raw));
      else if (raw.trim() !== '') throw new XmlError('text outside the root element');
      continue;
    }
    if (text.startsWith('<!--', pos)) {
      const end = text.indexOf('-->', pos + 4);
      if (end < 0) throw new XmlError('unterminated comment');
      pos = end + 3;
    } else if (text.startsWith('<![CDATA[', pos)) {
      const end = text.indexOf(']]>', pos + 9);
      const top = stack[stack.length - 1];
      if (end < 0 || !top) throw new XmlError('bad CDATA section');
      top.el.kids.push(text.slice(pos + 9, end));
      pos = end + 3;
    } else if (text.startsWith('<!', pos)) {
      throw new DtdError('DTDs are not allowed');
    } else if (text.startsWith('<?', pos)) {
      const end = text.indexOf('?>', pos + 2);
      if (end < 0) throw new XmlError('unterminated processing instruction');
      pos = end + 2;
    } else if (text.startsWith('</', pos)) {
      const end = text.indexOf('>', pos);
      const name = end < 0 ? '' : text.slice(pos + 2, end).trim();
      const top = stack.pop();
      if (!top || top.name !== name) throw new XmlError(`mismatched closing tag '${name}'`);
      pos = end + 1;
    } else {
      NAME.lastIndex = pos + 1;
      const m = NAME.exec(text);
      if (!m) throw new XmlError('malformed tag');
      const qname = m[0];
      pos += 1 + qname.length;
      const raw = new Map<string, string>();
      let selfClose = false;
      for (;;) {
        while (pos < text.length && /\s/.test(text[pos])) pos++;
        if (pos >= text.length) throw new XmlError('unterminated tag');
        if (text[pos] === '>') { pos++; break; }
        if (text[pos] === '/' && text[pos + 1] === '>') { selfClose = true; pos += 2; break; }
        NAME.lastIndex = pos;
        const a = NAME.exec(text);
        if (!a) throw new XmlError('malformed attribute');
        pos += a[0].length;
        while (pos < text.length && /\s/.test(text[pos])) pos++;
        if (text[pos] !== '=') throw new XmlError('attribute without a value');
        pos++;
        while (pos < text.length && /\s/.test(text[pos])) pos++;
        const q = text[pos];
        if (q !== '"' && q !== "'") throw new XmlError('unquoted attribute value');
        const end = text.indexOf(q, pos + 1);
        if (end < 0) throw new XmlError('unterminated attribute value');
        if (raw.has(a[0])) throw new XmlError(`duplicate attribute '${a[0]}'`);
        raw.set(a[0], decode(text.slice(pos + 1, end)));
        pos = end + 1;
      }
      if (stack.length >= MAX_DEPTH) throw new XmlError('too deeply nested');
      if (root && stack.length === 0) throw new XmlError('more than one root element');
      let scope = stack.length ? stack[stack.length - 1].scope : baseScope;
      if ([...raw.keys()].some(k => k === 'xmlns' || k.startsWith('xmlns:'))) {
        scope = new Map(scope);
        for (const [k, v] of raw) if (k === 'xmlns') scope.set('', v); else if (k.startsWith('xmlns:')) scope.set(k.slice(6), v);
      }
      const colon = qname.indexOf(':');
      const prefix = colon < 0 ? '' : qname.slice(0, colon);
      const ns = scope.get(prefix);
      if (ns === undefined && prefix !== '') throw new XmlError(`undeclared prefix '${prefix}'`);
      const el: XEl = { ns: ns ?? '', local: colon < 0 ? qname : qname.slice(colon + 1), attrs: new Map(), kids: [] };
      for (const [k, v] of raw) {
        if (k === 'xmlns' || k.startsWith('xmlns:')) continue;
        const c = k.indexOf(':');
        if (c < 0) { el.attrs.set(k, v); continue; }
        const uri = scope.get(k.slice(0, c));
        if (uri === undefined) throw new XmlError(`undeclared prefix '${k.slice(0, c)}'`);
        el.attrs.set(attrKey(uri, k.slice(c + 1)), v);
      }
      if (stack.length) stack[stack.length - 1].el.kids.push(el);
      else root = el;
      if (!selfClose) stack.push({ el, scope, name: qname });
    }
  }
  if (stack.length) throw new XmlError('unclosed element');
  if (!root) throw new XmlError('no root element');
  return root;
}

// ---------- querying ----------

export const elements = (e: XEl | null | undefined, ns?: string, local?: string): XEl[] =>
  e ? e.kids.filter((k): k is XEl => typeof k !== 'string' && (ns === undefined || (k.ns === ns && k.local === local))) : [];
export const child = (e: XEl | null | undefined, ns: string, local: string): XEl | undefined => elements(e, ns, local)[0];

/** Descendants in document order (not including `e`), optionally only of one name. */
export function descendants(e: XEl | null | undefined, ns?: string, local?: string, self = false): XEl[] {
  const out: XEl[] = [];
  if (!e) return out;
  const add = (x: XEl) => { if (ns === undefined || (x.ns === ns && x.local === local)) out.push(x); };
  if (self) add(e);
  const walk: [XEl, number][] = [[e, 0]];
  while (walk.length) {
    const top = walk[walk.length - 1];
    if (top[1] >= top[0].kids.length) { walk.pop(); continue; }
    const k = top[0].kids[top[1]++];
    if (typeof k === 'string') continue;
    add(k);
    walk.push([k, 0]);
  }
  return out;
}

/** The concatenated text of an element and everything under it. */
export function textOf(e: XEl): string {
  let s = '';
  const walk: [XEl, number][] = [[e, 0]];
  while (walk.length) {
    const top = walk[walk.length - 1];
    if (top[1] >= top[0].kids.length) { walk.pop(); continue; }
    const k = top[0].kids[top[1]++];
    if (typeof k === 'string') s += k; else walk.push([k, 0]);
  }
  return s;
}

export const attr = (e: XEl | null | undefined, ns: string, local: string): string | undefined => e?.attrs.get(attrKey(ns, local));
