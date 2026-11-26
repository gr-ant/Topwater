// to-markdown: converts documents in the virtual filesystem to Markdown (docx, xlsx, pptx, html, csv/tsv, json, md/txt)
// with no dependencies. PDF is not converted: that needs the host's pdftotext, and nothing here starts a process.
// Malformed input throws ShellError, never anything else.

import { OutputLimitError, inflateRaw } from '../inflate.js';
import { Args, specOf, specMessage, ShellError, reason, type CommandContext } from '../command.js';
import type { VirtualFileSystem } from '../vfs.js';
import type { Shell } from '../shell.js';
import { ZipFormatError, readZip, type ZipEntry } from './zipfile.js';
import { DtdError, XmlError, attr, child, descendants, elements, parseXml, textOf, type XEl } from './xml.js';

/** Zip-bomb guard: total bytes read out of one archive. */
export const converterLimits = { maxUncompressedBytes: 100 * 1024 * 1024 };

const SUPPORTED = '.docx, .xlsx, .pptx, .pdf, .html, .htm, .csv, .tsv, .json, .md, .txt';
const W = 'http://schemas.openxmlformats.org/wordprocessingml/2006/main';
const S = 'http://schemas.openxmlformats.org/spreadsheetml/2006/main';
const A = 'http://schemas.openxmlformats.org/drawingml/2006/main';
const P = 'http://schemas.openxmlformats.org/presentationml/2006/main';
const R = 'http://schemas.openxmlformats.org/officeDocument/2006/relationships';

const utf8 = (d: Uint8Array) => new TextDecoder().decode(d);

type Converter = (data: Uint8Array) => string;
const CONVERTERS: Record<string, Converter> = {
  '.docx': d => docx(new Pkg(d)), '.xlsx': d => xlsx(new Pkg(d)), '.pptx': d => pptx(new Pkg(d)),
  '.html': html, '.pdf': pdf, '.csv': d => delimited(d, ','), '.tsv': d => delimited(d, '\t'), '.json': json,
  '.md': utf8, '.txt': utf8,
};

/** Converts a document to Markdown. The format comes from the file name's extension, falling back to magic bytes. */
export function toMarkdown(data: Uint8Array, fileName: string): string {
  const base = fileName.slice(fileName.lastIndexOf('/') + 1).toLowerCase();
  const dot = base.lastIndexOf('.');
  const ext = dot < 0 ? '' : base.slice(dot);
  let fmt = ext === '.htm' || ext === '.xhtml' ? '.html' : ext === '.markdown' ? '.md' : ext;
  if (!Object.hasOwn(CONVERTERS, fmt)) {
    const sniffed = sniff(data);
    if (!sniffed) throw new ShellError(specMessage('to-markdown', 'unsupportedFormat', { format: ext || fileName, supported: SUPPORTED }));
    fmt = sniffed;
  }
  try { return CONVERTERS[fmt](data); } catch (e) {
    if (e instanceof ShellError) throw e;
    if (e instanceof DtdError) throw new ShellError(`${e.message}`);
    // corrupt zip/XML/encoding: report, never crash the host
    throw new ShellError(specMessage('to-markdown', 'invalidFile', { format: fmt.slice(1), detail: e instanceof ZipFormatError || e instanceof XmlError || e instanceof Error ? e.message : String(e) }));
  }
}

const startsWith = (data: Uint8Array, text: string) => data.length >= text.length && [...text].every((c, i) => data[i] === c.charCodeAt(0));

function sniff(data: Uint8Array): string | null {
  if (startsWith(data, '%PDF-')) return '.pdf';
  if (startsWith(data, 'PK')) {
    let names: Set<string>;
    try { names = new Set(readZip(data).map(e => e.name)); } catch { return null; }
    for (const [part, fmt] of [['word/document.xml', '.docx'], ['xl/workbook.xml', '.xlsx'], ['ppt/presentation.xml', '.pptx']]) if (names.has(part)) return fmt;
    return null;
  }
  const head = String.fromCharCode(...data.subarray(0, 1024)).trimStart().toLowerCase();
  if (head.startsWith('<!doctype html') || head.startsWith('<html')) return '.html';
  if (head.startsWith('{') || head.startsWith('[')) {
    try { JSON.parse(utf8(data)); return '.json'; } catch { /* not JSON */ }
  }
  return null;
}

// ---------- shared helpers ----------

/** A zip (OOXML package) read with a cap on total uncompressed bytes and no DTDs/entities in XML parts. */
class Pkg {
  private readonly entries = new Map<string, ZipEntry>();
  private budget = converterLimits.maxUncompressedBytes;
  constructor(data: Uint8Array) { for (const e of readZip(data)) this.entries.set(e.name, e); }

  read(name: string): Uint8Array | null {
    const e = name ? this.entries.get(name) : undefined;
    if (!e) return null;
    let out: Uint8Array;
    if (e.method === 0) out = e.data;
    else if (e.method === 8) {
      try { out = inflateRaw(e.data, Math.max(1, this.budget)); } catch (err) {
        if (err instanceof OutputLimitError) throw new ShellError(specMessage('to-markdown', 'archiveTooLarge', { megabytes: String(Math.floor(converterLimits.maxUncompressedBytes / (1024 * 1024))) }));
        throw err;
      }
    } else throw new XmlError(`unsupported compression method ${e.method}`);
    if (out.length > this.budget) throw new ShellError(specMessage('to-markdown', 'archiveTooLarge', { megabytes: String(Math.floor(converterLimits.maxUncompressedBytes / (1024 * 1024))) }));
    this.budget -= out.length;
    return out;
  }

  xml(name: string): XEl | null {
    const bytes = this.read(name);
    if (!bytes) return null;
    try { return parseXml(utf8(bytes)); } catch (e) {
      if (e instanceof DtdError) throw new ShellError(specMessage('to-markdown', 'dtdNotAllowed', { name: name }));
      throw e;
    }
  }

  /** Relationship id -> absolute part name (or external URL) for a part. */
  rels(part: string): Map<string, string> {
    const slash = part.lastIndexOf('/');
    const dir = slash < 0 ? '' : part.slice(0, slash), name = part.slice(slash + 1);
    const result = new Map<string, string>();
    const root = this.xml((dir ? dir + '/' : '') + '_rels/' + name + '.rels');
    if (!root) return result;
    for (const r of elements(root)) {
      let target = r.attrs.get('Target') ?? '';
      if (r.attrs.get('TargetMode') !== 'External') target = target.startsWith('/') ? target.slice(1) : normPath(dir ? dir + '/' + target : target);
      result.set(r.attrs.get('Id') ?? '', target);
    }
    return result;
  }
}

function normPath(path: string): string {
  const stack: string[] = [];
  for (const seg of path.split('/')) {
    if (seg === '' || seg === '.') continue;
    if (seg === '..') stack.pop(); else stack.push(seg);
  }
  return stack.join('/');
}

const squash = (s: string) => s.split(/\s+/).filter(Boolean).join(' ');

/** Pipe table; the first row is the header. */
function table(rows: string[][]): string {
  const cells = rows.map(r => r.map(c => squash(c).replace(/\|/g, '\\|')));
  const width = cells.reduce((m, r) => Math.max(m, r.length), 0);
  if (width === 0) return '';
  const lines = cells.map(r => '| ' + [...r, ...Array(width - r.length).fill('')].join(' | ') + ' |');
  lines.splice(1, 0, '|' + ' --- |'.repeat(width));
  return lines.join('\n');
}

/** Wraps text in an emphasis marker, keeping surrounding whitespace outside it. */
function wrap(text: string, marker: string): string {
  const core = text.trim();
  if (!core) return text;
  const start = text.length - text.trimStart().length;
  return text.slice(0, start) + marker + core + marker + text.slice(start + core.length);
}

type Block = [text: string, list: string | null];

/** Consecutive items of one list stay tight, all else gets a blank line. */
function joinBlocks(blocks: Block[]): string {
  const kept = blocks.filter(b => b[0].length > 0);
  if (kept.length === 0) return '';
  let s = '';
  kept.forEach((b, i) => {
    if (i > 0) s += b[1] !== null && b[1] === kept[i - 1][1] ? '\n' : '\n\n';
    s += b[0];
  });
  return s.replace(/^\n+|\n+$/g, '') + '\n';
}

const level = (s: string | undefined) => s !== undefined && /^\d+$/.test(s) ? Math.min(Number(s), 8) : 0;
const indent = (n: number) => ' '.repeat(3 * n);

// ---------- docx ----------

/** w:val of el's child element (e.g. `<w:pStyle w:val="Heading1"/>`). */
const val = (el: XEl | null | undefined, name: string, fallback: string) => {
  const c = child(el, W, name);
  return c ? attr(c, W, 'val') ?? fallback : fallback;
};

/** Expands content controls (w:sdt) into their block content, keeping document order. */
function flattenSdt(elems: XEl[]): XEl[] {
  const result: XEl[] = [];
  const stack = [...elems].reverse();
  while (stack.length) {
    const el = stack.pop()!;
    const content = el.ns === W && el.local === 'sdt' ? child(el, W, 'sdtContent') : undefined;
    if (content) stack.push(...elements(content).reverse()); else result.push(el);
  }
  return result;
}

function docx(pkg: Pkg): string {
  const doc = pkg.xml('word/document.xml');
  if (!doc) throw new ShellError(specMessage('to-markdown', 'partMissing', { format: 'docx', part: 'word/document.xml' }));
  const rels = pkg.rels('word/document.xml');
  const styles = new Map<string, { name: string; num: [string, string] | null }>();
  for (const s of elements(pkg.xml('word/styles.xml'), W, 'style')) {
    const num = child(child(s, W, 'pPr'), W, 'numPr');
    styles.set(attr(s, W, 'styleId') ?? '', { name: (attr(child(s, W, 'name'), W, 'val') ?? '').toLowerCase(), num: num ? [val(num, 'numId', '0'), val(num, 'ilvl', '0')] : null });
  }
  const formats = new Map<string, string>(); // numId\0ilvl -> numFmt
  const numbering = pkg.xml('word/numbering.xml');
  if (numbering) {
    const abstracts = new Map<string, XEl>();
    for (const a of elements(numbering, W, 'abstractNum')) abstracts.set(attr(a, W, 'abstractNumId') ?? '', a);
    for (const n of elements(numbering, W, 'num')) {
      const a = abstracts.get(val(n, 'abstractNumId', ''));
      if (a) for (const lvl of elements(a, W, 'lvl')) formats.set(`${attr(n, W, 'numId') ?? ''}\0${attr(lvl, W, 'ilvl') ?? ''}`, val(lvl, 'numFmt', 'bullet'));
    }
  }

  const counters = new Map<string, number[]>();
  const blocks: Block[] = [];
  for (const el of flattenSdt(elements(child(doc, W, 'body')))) {
    if (el.ns === W && el.local === 'tbl') {
      blocks.push([table(descendants(el, W, 'tr').map(tr => descendants(tr, W, 'tc').map(tc => docxCell(tc, rels)))), null]);
      continue;
    }
    if (!(el.ns === W && el.local === 'p')) continue;
    const text = docxInline(el, rels, 0).trim();
    if (!text) continue;
    const pPr = child(el, W, 'pPr');
    const styleId = val(pPr, 'pStyle', '');
    const style = styles.get(styleId) ?? { name: '', num: null };
    const label = (styleId + ' ' + style.name).toLowerCase();
    const heading = /heading\s*([1-6])/.exec(label);
    if (heading || (/\btitle\b/.test(label) && !label.includes('subtitle'))) {
      blocks.push(['#'.repeat(heading ? Number(heading[1]) : 1) + ' ' + squash(text), null]);
      continue;
    }
    const numPr = child(pPr, W, 'numPr');
    let [numId, lvl] = numPr ? [val(numPr, 'numId', '0'), val(numPr, 'ilvl', '0')] : style.num ?? ['0', '0'];
    if (numId === '0' && (style.name.includes('list bullet') || style.name.includes('list number'))) numId = style.name.includes('list number') ? 'list number' : 'list bullet';
    if (numId === '0') { blocks.push([text, null]); continue; }
    const ilvl = level(lvl);
    const fmt = formats.get(`${numId}\0${ilvl}`) ?? (numId === 'list number' ? 'decimal' : 'bullet');
    let c = counters.get(numId);
    if (!c) counters.set(numId, c = new Array(9).fill(0));
    c[ilvl]++;
    for (let k = ilvl + 1; k < 9; k++) c[k] = 0;
    const marker = fmt === 'bullet' || fmt === 'none' ? '-' : `${c[ilvl]}.`;
    blocks.push([indent(ilvl) + marker + ' ' + text.replace(/\n/g, '\n' + indent(ilvl + 1)), 'list ' + numId]);
  }
  return joinBlocks(blocks);
}

const docxCell = (tc: XEl, rels: Map<string, string>) => descendants(tc, W, 'p').map(p => docxInline(p, rels, 0)).join(' ');

const on = (rpr: XEl | undefined, prop: string) => {
  const el = child(rpr, W, prop);
  return !!el && !['0', 'false', 'off', 'none'].includes((attr(el, W, 'val') ?? 'true').toLowerCase());
};

function docxInline(container: XEl, rels: Map<string, string>, depth: number): string {
  // links etc. are pre-rendered with null formatting
  const segments: { text: string; bold: boolean | null; italic: boolean | null }[] = [];
  const add = (text: string, bold: boolean | null, italic: boolean | null) => {
    const last = segments[segments.length - 1];
    if (last && bold !== null && last.bold === bold && last.italic === italic) last.text += text;
    else segments.push({ text, bold, italic });
  };
  for (const el of elements(container)) {
    if (el.ns !== W) continue;
    if (el.local === 'r') {
      const rpr = child(el, W, 'rPr');
      const b = on(rpr, 'b'), i = on(rpr, 'i');
      for (const t of elements(el)) {
        if (t.ns !== W) continue;
        if (t.local === 't') add(textOf(t), b, i);
        else if (t.local === 'tab') add('\t', b, i);
        else if (t.local === 'br' || t.local === 'cr') add('\n', b, i);
      }
    } else if (el.local === 'hyperlink' && depth < 32) {
      const text = docxInline(el, rels, depth + 1);
      const id = attr(el, R, 'id'), anchor = attr(el, W, 'anchor');
      const url = id !== undefined ? rels.get(id) ?? '' : anchor !== undefined ? '#' + anchor : '';
      add(url && text.trim() ? `[${text}](${url})` : text, null, null);
    } else if (['ins', 'smartTag', 'fldSimple', 'customXml'].includes(el.local) && depth < 32) add(docxInline(el, rels, depth + 1), null, null);
    else if (el.local === 'sdt' && depth < 32) { const c = child(el, W, 'sdtContent'); if (c) add(docxInline(c, rels, depth + 1), null, null); }
  }
  return segments.map(({ text, bold, italic }) => { const t = italic ? wrap(text, '*') : text; return bold ? wrap(t, '**') : t; }).join('');
}

// ---------- xlsx ----------

const MAX_SHEET_CELLS = 5_000_000;

function xlsx(pkg: Pkg): string {
  const wb = pkg.xml('xl/workbook.xml');
  if (!wb) throw new ShellError(specMessage('to-markdown', 'partMissing', { format: 'xlsx', part: 'xl/workbook.xml' }));
  const rels = pkg.rels('xl/workbook.xml');
  const shared: string[] = [];
  for (const si of elements(pkg.xml('xl/sharedStrings.xml'))) {
    const phonetic = new Set(descendants(si, S, 'rPh').flatMap(r => descendants(r, S, 't')));
    shared.push(descendants(si, S, 't').filter(t => !phonetic.has(t)).map(textOf).join(''));
  }
  const parts: string[] = [];
  for (const sheet of descendants(wb, S, 'sheet')) {
    const root = pkg.xml(rels.get(attr(sheet, R, 'id') ?? '') ?? '');
    const rows: Map<number, string>[] = [];
    for (const row of descendants(root, S, 'row')) {
      const cells = new Map<number, string>();
      let col = -1;
      for (const c of elements(row, S, 'c')) {
        col++;
        const m = /^([A-Z]+)/.exec(c.attrs.get('r') ?? '');
        if (m && m[1].length > 3) continue; // past Excel's last column
        if (m) col = [...m[1]].reduce((n, ch) => n * 26 + ch.charCodeAt(0) - 64, 0) - 1;
        const value = xlsxValue(c, shared);
        if (value) cells.set(col, value);
      }
      if (cells.size > 0) rows.push(cells);
    }
    const first = rows.length ? rows.reduce((m, r) => { for (const k of r.keys()) m = Math.min(m, k); return m; }, Infinity) : 0;
    const last = rows.length ? rows.reduce((m, r) => { for (const k of r.keys()) m = Math.max(m, k); return m; }, -Infinity) : -1;
    const name = sheet.attrs.get('name') ?? '';
    if ((last - first + 1) * rows.length > MAX_SHEET_CELLS) throw new ShellError(specMessage('to-markdown', 'sheetTooLarge', { name: name }));
    const t = table(rows.map(r => Array.from({ length: last - first + 1 }, (_, k) => r.get(first + k) ?? '')));
    parts.push(`## ${name}` + (t ? '\n\n' + t : ''));
  }
  return parts.length ? parts.join('\n\n') + '\n' : '';
}

function xlsxValue(c: XEl, shared: string[]): string {
  const t = c.attrs.get('t') ?? 'n';
  const v = child(c, S, 'v');
  if (t === 'inlineStr') return descendants(c, S, 't').map(textOf).join('');
  if (!v) { const f = child(c, S, 'f'); return f && textOf(f) ? '=' + textOf(f) : ''; } // formula without a cached value
  const text = textOf(v);
  if (t === 's') { const i = /^-?\d+$/.test(text) ? Number(text) : -1; return i >= 0 && i < shared.length ? shared[i] : ''; }
  if (t === 'b') return text === '1' ? 'TRUE' : 'FALSE';
  return text;
}

// ---------- pptx ----------

function pptx(pkg: Pkg): string {
  const pres = pkg.xml('ppt/presentation.xml');
  if (!pres) throw new ShellError(specMessage('to-markdown', 'partMissing', { format: 'pptx', part: 'ppt/presentation.xml' }));
  const rels = pkg.rels('ppt/presentation.xml');
  const parts: string[] = [];
  let n = 0;
  for (const sld of descendants(pres, P, 'sldId')) {
    n++;
    const name = rels.get(attr(sld, R, 'id') ?? '') ?? '';
    const slide = pkg.xml(name);
    if (!slide) continue;
    const { title, blocks } = pptxShapes(slide, false);
    const output = [`## ${title ?? `Slide ${n}`}`];
    if (blocks.length) output.push(joinBlocks(blocks).replace(/\n+$/, ''));
    const notesPart = [...pkg.rels(name).values()].find(t => t.endsWith('.xml') && t.includes('notesSlide'));
    const notes = notesPart ? pkg.xml(notesPart) : null;
    if (notes) {
      const noteBlocks = pptxShapes(notes, true).blocks;
      if (noteBlocks.length) output.push('### Notes\n\n' + joinBlocks(noteBlocks).replace(/\n+$/, ''));
    }
    parts.push(output.join('\n\n'));
  }
  return parts.length ? parts.join('\n\n') + '\n' : '';
}

/** Returns (title text or null, blocks) for a slide or a notes page, skipping slide numbers, footers and dates. */
function pptxShapes(slide: XEl, notes: boolean): { title: string | null; blocks: Block[] } {
  let title: string | null = null;
  const blocks: Block[] = [];
  for (const el of descendants(slide, undefined, undefined, true)) {
    if (el.ns === A && el.local === 'tbl') {
      blocks.push([table(descendants(el, A, 'tr').map(tr => descendants(tr, A, 'tc').map(tc => descendants(tc, A, 'p').map(pptxText).join(' ')))), null]);
      continue;
    }
    if (!(el.ns === P && el.local === 'sp')) continue;
    const ph = child(child(child(el, P, 'nvSpPr'), P, 'nvPr'), P, 'ph');
    const kind = ph ? ph.attrs.get('type') ?? 'body' : null;
    const body = child(el, P, 'txBody');
    if (!body || (kind !== null && ['sldNum', 'ftr', 'dt', 'hdr', 'sldImg'].includes(kind))) continue;
    const paras = elements(body, A, 'p').map(p => ({ p, text: pptxText(p) }));
    if ((kind === 'title' || kind === 'ctrTitle') && title === null && !notes) {
      title = squash(paras.map(p => p.text).join(' '));
      continue;
    }
    for (const { p, text } of paras) {
      if (!text.trim()) continue;
      const ppr = child(p, A, 'pPr');
      const lvl = level(ppr?.attrs.get('lvl'));
      const explicitBullet = !!child(ppr, A, 'buChar') || !!child(ppr, A, 'buAutoNum');
      const off = !!child(ppr, A, 'buNone');
      const bullet = !notes && !off && (explicitBullet || kind === 'body' || kind === 'obj');
      blocks.push(bullet ? [indent(lvl) + '- ' + text, 'bullet'] : [text, null]);
    }
  }
  return { title, blocks };
}

const pptxText = (p: XEl) => elements(p).map(el =>
  el.ns === A && (el.local === 'r' || el.local === 'fld') ? textOf(child(el, A, 't') ?? { ns: '', local: '', attrs: new Map(), kids: [] }) : el.ns === A && el.local === 'br' ? '\n' : '').join('');

// ---------- html ----------

const NAMED: Record<string, string> = {
  amp: '&', lt: '<', gt: '>', quot: '"', apos: "'", nbsp: ' ', copy: '©', reg: '®', trade: '™', hellip: '…', mdash: '—', ndash: '–',
  lsquo: '‘', rsquo: '’', ldquo: '“', rdquo: '”', bull: '•', middot: '·', euro: '€', pound: '£', yen: '¥', cent: '¢', sect: '§', deg: '°',
  plusmn: '±', times: '×', divide: '÷', laquo: '«', raquo: '»', para: '¶', larr: '←', rarr: '→', uarr: '↑', darr: '↓', hearts: '♥',
};
function htmlDecode(s: string): string {
  if (!s.includes('&')) return s;
  return s.replace(/&(#x[0-9a-fA-F]+|#[0-9]+|[A-Za-z][A-Za-z0-9]*);/g, (whole, e: string) => {
    if (e[0] === '#') {
      const code = e[1] === 'x' || e[1] === 'X' ? parseInt(e.slice(2), 16) : parseInt(e.slice(1), 10);
      return code > 0 && code <= 0x10ffff && !(code >= 0xd800 && code <= 0xdfff) ? String.fromCodePoint(code) : '�';
    }
    return NAMED[e] ?? whole;
  });
}

const HTML_ATTR = /([^\s=/>"']+)(?:\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s>"']+)))?/g;

function html(data: Uint8Array): string {
  const text = utf8(data);
  const h = new HtmlState();
  let pos = 0;
  // an unterminated quote means no later quote of that kind can close either, so each failure is looked up once
  const noQuoteAfter: Record<string, number> = { '"': Infinity, "'": Infinity };
  /** The end of the tag starting at `<` + a letter, with its name and attribute text; null if it never closes. */
  const scanTag = (start: number): { end: number; name: string; attrs: string; closing: boolean } | null => {
    const m = /^<(\/?)([A-Za-z][A-Za-z0-9:-]*)/.exec(text.slice(start, start + 256));
    if (!m) return null;
    let i = start + m[0].length;
    const attrsFrom = i;
    const limit = Math.min(text.length, start + 16384); // no real tag is longer; keeps a pile of unclosed tags linear
    while (i < limit) {
      const c = text[i];
      if (c === '>') return { end: i + 1, name: m[2], attrs: text.slice(attrsFrom, i), closing: m[1] === '/' };
      if (c === '"' || c === "'") {
        if (i >= noQuoteAfter[c]) return null;
        const close = text.indexOf(c, i + 1);
        if (close < 0) { noQuoteAfter[c] = i; return null; }
        if (close >= limit) return null;
        i = close + 1;
      } else i++;
    }
    return null;
  };
  while (pos < text.length) {
    if (text[pos] !== '<') {
      const end = text.indexOf('<', pos);
      h.data(htmlDecode(text.slice(pos, end < 0 ? text.length : end)));
      pos = end < 0 ? text.length : end;
      continue;
    }
    if (text.startsWith('<!--', pos)) {
      const end = text.indexOf('-->', pos + 4);
      if (end >= 0) { pos = end + 3; continue; }
    }
    if (text[pos + 1] === '!' || text[pos + 1] === '?') {
      const end = text.indexOf('>', pos);
      if (end >= 0) { pos = end + 1; continue; }
      h.data('<'); pos++;
      continue;
    }
    const tag = scanTag(pos);
    if (!tag) { h.data('<'); pos++; continue; }
    pos = tag.end;
    const name = tag.name.toLowerCase();
    if (tag.closing) { h.end(name); continue; }
    const attrs = new Map<string, string>();
    for (const a of tag.attrs.matchAll(HTML_ATTR)) attrs.set(a[1].toLowerCase(), htmlDecode(a[2] ?? a[3] ?? a[4] ?? ''));
    h.start(name, attrs);
    if (tag.attrs.trimEnd().endsWith('/')) h.end(name);
    else if (name === 'script' || name === 'style') { // raw text: skip to the closing tag
      const close = text.toLowerCase().indexOf('</' + name, pos);
      pos = close < 0 ? text.length : close;
    }
  }
  if (h.preBuf) h.end('pre');
  if (h.tableRows) { h.tableDepth = 0; h.end('table'); }
  h.flush();
  return joinBlocks(h.blocks);
}

const HEADINGS = new Set(['h1', 'h2', 'h3', 'h4', 'h5', 'h6']);
const SKIP = new Set(['script', 'style', 'noscript', 'template', 'head', 'nav', 'svg', 'iframe']);
const BLOCK = new Set(['p', 'div', 'section', 'article', 'main', 'header', 'footer', 'aside', 'blockquote', 'figure', 'figcaption',
  'dl', 'dt', 'dd', 'address', 'form', 'fieldset', 'details', 'summary', 'body', 'html', 'tr', 'table', 'caption']);
const INLINE = new Set(['a', 'strong', 'b', 'em', 'i', 'code']);
const BR = '\0'; // placeholder for <br> that survives whitespace collapsing

/** Streams tags into Markdown without recursion (so deeply nested input can't overflow the stack). */
class HtmlState {
  readonly blocks: Block[] = [];
  private buf: string[] = [];
  private inline: { tag: string; pos: number; href: string }[] = [];
  private readonly lists: { kind: string; count: number }[] = [];
  private listId = 0; // numbers top-level lists so two adjacent lists don't run together
  private prefix: string | null = null; // list marker waiting for the current <li>'s first text
  private skip = 0;
  preBuf: string[] | null = null;
  tableRows: string[][] | null = null;
  tableDepth = 0;

  flush(heading = 0) {
    let text = this.buf.join('').replace(/[ \t\r\n\f\v]+/g, ' ').replace(/^ +| +$/g, '');
    text = text.split(BR).map(l => l.trim()).join('\n').replace(/^\n+|\n+$/g, '');
    this.buf = [];
    if (!text) return;
    if (heading > 0) this.blocks.push(['#'.repeat(heading) + ' ' + squash(text), null]);
    else if (this.lists.length > 0) {
      const ind = indent(this.lists.length);
      const first = this.prefix;
      this.prefix = null;
      this.blocks.push([(first ?? ind) + text.replace(/\n/g, '\n' + ind), String(this.listId)]);
    } else this.blocks.push([text, null]);
  }

  private static image = (a: Map<string, string>) => `![${a.get('alt') ?? ''}](${a.get('src') ?? ''})`;

  start(tag: string, a: Map<string, string>) {
    if (SKIP.has(tag)) { this.skip++; return; }
    if (this.skip > 0) return;
    if (this.preBuf) { if (tag === 'br') this.preBuf.push('\n'); return; }
    if (this.tableRows) {
      if (tag === 'table') this.tableDepth++;
      else if (this.tableDepth === 0 && tag === 'tr') this.tableRows.push([]);
      else if (this.tableDepth === 0 && (tag === 'td' || tag === 'th')) { this.buf = []; this.inline = []; }
      else if (tag === 'br') this.buf.push(' ');
      else if (tag === 'img') this.buf.push(HtmlState.image(a));
      else if (INLINE.has(tag)) this.inline.push({ tag, pos: this.buf.length, href: a.get('href') ?? '' });
      return;
    }
    if (HEADINGS.has(tag) || tag === 'pre' || tag === 'table' || tag === 'hr' || BLOCK.has(tag)) this.flush();
    if (tag === 'pre') this.preBuf = [];
    else if (tag === 'table') this.tableRows = [];
    else if (tag === 'hr') this.blocks.push(['---', null]);
    else if (tag === 'ul' || tag === 'ol') {
      this.flush();
      if (this.lists.length === 0) this.listId++;
      const start = a.get('start') ?? '';
      this.lists.push({ kind: tag, count: /^\d+$/.test(start) ? Number(start) - 1 : 0 });
    } else if (tag === 'li') {
      this.flush();
      const top = this.lists[this.lists.length - 1];
      if (top) { top.count++; this.prefix = indent(this.lists.length - 1) + (top.kind === 'ul' ? '-' : `${top.count}.`) + ' '; }
    } else if (tag === 'br') this.buf.push(BR);
    else if (tag === 'img') this.buf.push(HtmlState.image(a));
    else if (INLINE.has(tag)) this.inline.push({ tag, pos: this.buf.length, href: a.get('href') ?? '' });
  }

  end(tag: string) {
    if (SKIP.has(tag)) { this.skip = Math.max(0, this.skip - 1); return; }
    if (this.skip > 0) return;
    if (this.preBuf) {
      if (tag === 'pre') {
        const code = this.preBuf.join('').replace(/^\n+|\n+$/g, '');
        this.preBuf = null;
        this.blocks.push(['```\n' + code + '\n```', null]);
      }
      return;
    }
    if (this.tableRows) {
      if (tag === 'table' && this.tableDepth > 0) this.tableDepth--;
      else if (tag === 'table') {
        const rows = this.tableRows.filter(r => r.length > 0);
        this.tableRows = null;
        if (rows.length) this.blocks.push([table(rows), null]);
      } else if (this.tableDepth === 0 && (tag === 'td' || tag === 'th')) {
        if (this.tableRows.length === 0) this.tableRows.push([]);
        this.tableRows[this.tableRows.length - 1].push(this.buf.join(''));
        this.buf = [];
      }
      if (tag === 'table' || tag === 'tr' || tag === 'td' || tag === 'th') return;
    }
    if (!this.tableRows && HEADINGS.has(tag)) this.flush(Number(tag[1]));
    else if (!this.tableRows && (tag === 'ul' || tag === 'ol')) { this.flush(); this.lists.pop(); }
    else if (!this.tableRows && (tag === 'li' || BLOCK.has(tag))) this.flush();
    else {
      for (let k = this.inline.length - 1; k >= 0; k--) {
        const { tag: open, href } = this.inline[k];
        if (open !== tag) continue;
        const pos = Math.min(this.inline[k].pos, this.buf.length);
        this.inline.splice(k);
        const text = this.buf.slice(pos).join('');
        const wrapped = tag === 'a' ? (href && text.trim() && !href.startsWith('javascript:') ? `[${text.trim()}](${href})` : text)
          : tag === 'code' ? (text.trim() ? `\`${text}\`` : text)
          : wrap(text, tag === 'strong' || tag === 'b' ? '**' : '*');
        this.buf.splice(pos);
        this.buf.push(wrapped);
        break;
      }
    }
  }

  data(data: string) {
    if (this.skip > 0) return;
    (this.preBuf ?? this.buf).push(data);
  }
}

// ---------- plain formats ----------

function delimited(data: Uint8Array, delimiter: string): string {
  const text = utf8(data).replace(/^﻿+/, '');
  const rows: string[][] = [];
  let row: string[] = [];
  let field = '';
  let quoted = false, atStart = true, any = false;
  const endField = () => { row.push(field); field = ''; atStart = true; };
  const endRow = () => { if (any) { endField(); rows.push(row); } row = []; field = ''; any = false; atStart = true; };
  for (let i = 0; i < text.length; i++) {
    const c = text[i];
    if (quoted) {
      if (c !== '"') field += c;
      else if (text[i + 1] === '"') { field += '"'; i++; }
      else quoted = false;
      continue;
    }
    if (c === '"' && atStart) { quoted = true; atStart = false; any = true; }
    else if (c === delimiter) { endField(); any = true; }
    else if (c === '\n' || c === '\r') { if (c === '\r' && text[i + 1] === '\n') i++; endRow(); }
    else { field += c; atStart = false; any = true; }
  }
  endRow();
  return rows.length ? table(rows) + '\n' : '';
}

function json(data: Uint8Array): string {
  let value: unknown;
  try { value = JSON.parse(utf8(data).replace(/^﻿+/, '')); } catch (e) { throw new ShellError(specMessage('to-markdown', 'invalidFile', { format: 'json', detail: (e as Error).message })); }
  return '```json\n' + JSON.stringify(value, null, 2) + '\n```\n';
}

function pdf(_data: Uint8Array): string {
  // the C# version runs the host's pdftotext; nothing here may start a process
  throw new ShellError('PDF conversion is not available (it needs the host program pdftotext, which this build never runs)');
}

// ---------- the command, and mounting documents ----------

function run(ctx: CommandContext): number {
  const a = Args.parse(ctx.args, specOf('to-markdown'));
  if (a.positional.length === 0) throw new ShellError(specMessage('to-markdown', 'missingOperand', {}));
  let code = 0;
  const parts: string[] = [];
  for (const f of a.positional) {
    let md: string;
    try { md = toMarkdown(ctx.fs.readBytes(ctx.resolvePath(f)), f); } catch (e) {
      if (!(e instanceof ShellError)) throw e;
      // filesystem errors carry the path ("/abs/x: No such file..."); show the name as typed instead
      ctx.stderr.write(`${ctx.name}: ${specMessage('to-markdown', 'convertFailed', { file: f, reason: e.message.startsWith('/') ? reason(e) : e.message })}\n`);
      code = 1;
      continue;
    }
    if (md.length > 0 && !md.endsWith('\n')) md += '\n';
    parts.push(a.positional.length > 1 ? `# ${f}\n\n${md}` : md);
  }
  const text = parts.join('\n');
  const output = a.value('o');
  if (output !== undefined) ctx.fs.writeText(ctx.resolvePath(output), text); else ctx.stdout.write(text);
  return code;
}

export function registerDocuments(shell: Shell): void {
  shell.register('to-markdown', run);
}

/**
 * Stores a document's bytes at `path` (creating parent directories); with `alsoMarkdown`, also writes its Markdown
 * conversion to `<path>.md`. Call it from host code with an unrestricted view such as `shell.fs` (not `shell.fs.as('agent')`).
 */
export function addDocument(fs: VirtualFileSystem, path: string, bytes: Uint8Array, alsoMarkdown = false): void {
  const slash = path.lastIndexOf('/');
  fs.mkdir(slash > 0 ? path.slice(0, slash) : '/', true);
  fs.writeBytes(path, bytes);
  if (alsoMarkdown) fs.writeText(path + '.md', toMarkdown(bytes, path.slice(slash + 1)));
}
