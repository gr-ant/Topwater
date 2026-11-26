"""Document-to-Markdown conversion (docx, xlsx, pptx, html, csv/tsv, json, md/txt) with no dependencies, all in-process.

PDF is not converted: that needs a native parser, so a host that wants it attaches the real program
(`register_host_command(shell, "pdftotext")`). Malformed input raises ShellError, never anything else.
"""
from __future__ import annotations

import csv
import io
import json
import posixpath
import re
import xml.etree.ElementTree as ET
import zipfile
from html.parser import HTMLParser

from .commands import spec_message
from .vfs import ShellError

MAX_UNCOMPRESSED = 100 * 1024 * 1024  # zip-bomb guard: total bytes read out of one archive
MAX_SHEET_CELLS = 5_000_000
SUPPORTED = ".docx, .xlsx, .pptx, .html, .htm, .csv, .tsv, .json, .md, .txt"

_W = "{http://schemas.openxmlformats.org/wordprocessingml/2006/main}"
_S = "{http://schemas.openxmlformats.org/spreadsheetml/2006/main}"
_A = "{http://schemas.openxmlformats.org/drawingml/2006/main}"
_P = "{http://schemas.openxmlformats.org/presentationml/2006/main}"
_R = "{http://schemas.openxmlformats.org/officeDocument/2006/relationships}"
_REL = "{http://schemas.openxmlformats.org/package/2006/relationships}"


def to_markdown(data: bytes | str, filename: str) -> str:
    """Converts a document to Markdown. The format comes from filename's extension, falling back to magic bytes."""
    if isinstance(data, str):
        data = data.encode()
    ext = posixpath.splitext(filename.lower())[1]
    fmt = {".htm": ".html", ".xhtml": ".html", ".markdown": ".md"}.get(ext, ext)
    if fmt not in _CONVERTERS:
        fmt = _sniff(data)
    if fmt is None:
        raise ShellError(spec_message("to-markdown", "unsupportedFormat", format=ext or filename, supported=SUPPORTED))
    try:
        return _CONVERTERS[fmt](data)
    except ShellError:
        raise
    except Exception as e:  # corrupt zip/XML/encoding: report, never crash the host
        raise ShellError(spec_message("to-markdown", "invalidFile", format=fmt[1:], detail=str(e or type(e).__name__))) from None


def file_type(data: bytes) -> str | None:
    """`file`-style description of common binary formats, or None if data isn't one of them."""
    if data.startswith(b"%PDF-"):
        return f"PDF document, version {data[5:8].decode('latin-1')}"
    if data.startswith(b"\x89PNG\r\n\x1a\n") and len(data) >= 29:
        w, h, depth, color = int.from_bytes(data[16:20], "big"), int.from_bytes(data[20:24], "big"), data[24], data[25]
        kind = {0: " grayscale", 2: "/color RGB", 3: " colormap", 4: " gray+alpha", 6: "/color RGBA"}.get(color, "")
        return f"PNG image data, {w} x {h}, {depth}-bit{kind}, {'interlaced' if data[28] else 'non-interlaced'}"
    if data.startswith(b"\xff\xd8\xff"):
        return "JPEG image data"
    if data.startswith(b"SQLite format 3\0"):
        return "SQLite 3.x database"
    if data.startswith(b"\x1f\x8b"):
        return "gzip compressed data"
    if data.startswith(b"PK\x03\x04") or data.startswith(b"PK\x05\x06"):
        return {".docx": "Microsoft Word 2007+", ".xlsx": "Microsoft Excel 2007+",
                ".pptx": "Microsoft PowerPoint 2007+"}.get(_sniff(data), "Zip archive data")
    return None


def _sniff(data: bytes) -> str | None:
    if data.startswith(b"%PDF-"):
        return ".pdf"
    if data.startswith(b"PK"):
        try:
            names = set(zipfile.ZipFile(io.BytesIO(data)).namelist())
        except (zipfile.BadZipFile, ValueError, OSError):
            return None
        for part, fmt in (("word/document.xml", ".docx"), ("xl/workbook.xml", ".xlsx"), ("ppt/presentation.xml", ".pptx")):
            if part in names:
                return fmt
        return None
    head = data[:1024].lstrip().lower()
    if head.startswith((b"<!doctype html", b"<html")):
        return ".html"
    if head[:1] in (b"{", b"["):
        try:
            json.loads(data)
            return ".json"
        except ValueError:
            pass
    return None


# ---------- shared helpers ----------

class _Package:
    """A zip (OOXML package) read with a cap on total uncompressed bytes and no DTDs/entities in XML parts."""

    def __init__(self, data: bytes):
        self.zip = zipfile.ZipFile(io.BytesIO(data))
        self.budget = MAX_UNCOMPRESSED

    def read(self, name: str) -> bytes | None:
        try:
            info = self.zip.getinfo(name)
        except KeyError:
            return None
        with self.zip.open(info) as f:
            data = f.read(self.budget + 1)
        self.budget -= len(data)
        if self.budget < 0:
            raise ShellError(spec_message("to-markdown", "archiveTooLarge", megabytes=str(MAX_UNCOMPRESSED // (1024 * 1024))))
        return data

    def xml(self, name: str):
        data = self.read(name)
        if data is None:
            return None
        if b"<!DOCTYPE" in data or b"<!ENTITY" in data:
            raise ShellError(spec_message("to-markdown", "dtdNotAllowed", name=name))
        return ET.fromstring(data)

    def rels(self, part: str) -> dict[str, str]:
        """Relationship id -> absolute part name (or external URL) for a part."""
        d, base = posixpath.split(part)
        root = self.xml(posixpath.join(d, "_rels", base + ".rels"))
        out = {}
        for r in (root if root is not None else []):
            target = r.get("Target", "")
            if r.get("TargetMode") != "External":
                target = target[1:] if target.startswith("/") else posixpath.normpath(posixpath.join(d, target))
            out[r.get("Id")] = target
        return out


def _cell(text: str) -> str:
    return " ".join(text.split()).replace("|", "\\|")


def _table(rows: list[list[str]]) -> str:
    """Pipe table; the first row is the header."""
    rows = [[_cell(c) for c in r] for r in rows]
    width = max((len(r) for r in rows), default=0)
    if not width:
        return ""
    rows = [r + [""] * (width - len(r)) for r in rows]
    lines = ["| " + " | ".join(r) + " |" for r in rows]
    lines.insert(1, "|" + " --- |" * width)
    return "\n".join(lines)


def _wrap(text: str, marker: str) -> str:
    """Wraps text in an emphasis marker, keeping surrounding whitespace outside it."""
    core = text.strip()
    if not core:
        return text
    start = text.index(core)
    return f"{text[:start]}{marker}{core}{marker}{text[start + len(core):]}"


def _join_blocks(blocks: list[tuple[str, bool]]) -> str:
    """Blocks are (markdown, list id or False); consecutive items of one list stay tight, all else gets a blank line."""
    blocks = [b for b in blocks if b[0]]
    out = []
    for i, (text, item) in enumerate(blocks):
        if i:
            out.append("\n" if item and item == blocks[i - 1][1] else "\n\n")
        out.append(text)
    return "".join(out).strip("\n") + "\n" if blocks else ""


# ---------- docx ----------

def _docx(data: bytes) -> str:
    pkg = _Package(data)
    doc = pkg.xml("word/document.xml")
    if doc is None:
        raise ShellError(spec_message("to-markdown", "partMissing", format="docx", part="word/document.xml"))
    rels = pkg.rels("word/document.xml")
    styles: dict[str, tuple[str, tuple | None]] = {}  # styleId -> (name, (numId, ilvl) from the style)
    style_root = pkg.xml("word/styles.xml")
    for s in (style_root if style_root is not None else []):
        if s.tag == _W + "style":
            name = s.find(_W + "name")
            num = s.find(f"{_W}pPr/{_W}numPr")
            styles[s.get(_W + "styleId", "")] = (
                (name.get(_W + "val", "") if name is not None else "").lower(),
                (_val(num, "numId", "0"), _val(num, "ilvl", "0")) if num is not None else None)
    formats: dict[tuple[str, str], str] = {}  # (numId, ilvl) -> numFmt
    numbering = pkg.xml("word/numbering.xml")
    if numbering is not None:
        abstract = {a.get(_W + "abstractNumId"): a for a in numbering.findall(_W + "abstractNum")}
        for n in numbering.findall(_W + "num"):
            a = abstract.get(_val(n, "abstractNumId", ""))
            for lvl in (a if a is not None else []):
                if lvl.tag == _W + "lvl":
                    formats[(n.get(_W + "numId"), lvl.get(_W + "ilvl"))] = _val(lvl, "numFmt", "bullet")

    counters: dict[str, list[int]] = {}
    blocks: list[tuple[str, bool]] = []
    body = doc.find(_W + "body")
    for el in _flatten_sdt(list(body) if body is not None else []):
        if el.tag == _W + "tbl":
            blocks.append((_table([[_docx_cell(tc, rels) for tc in tr.iter(_W + "tc")] for tr in el.iter(_W + "tr")]), False))
            continue
        if el.tag != _W + "p":
            continue
        text = _docx_inline(el, rels).strip()
        if not text:
            continue
        style_id = _val(el.find(_W + "pPr"), "pStyle", "")
        style_name, style_num = styles.get(style_id, ("", None))
        label = (style_id + " " + style_name).lower()
        heading = re.search(r"heading\s*([1-6])", label)
        if heading or re.search(r"\btitle\b", label) and "subtitle" not in label:
            blocks.append(("#" * (int(heading.group(1)) if heading else 1) + " " + " ".join(text.split()), False))
            continue
        num = el.find(f"{_W}pPr/{_W}numPr")
        num_id, level = (_val(num, "numId", "0"), _val(num, "ilvl", "0")) if num is not None else style_num or ("0", "0")
        if num_id == "0" and ("list bullet" in style_name or "list number" in style_name):
            num_id = "list number" if "list number" in style_name else "list bullet"
        if num_id == "0":
            blocks.append((text, False))
            continue
        ilvl = min(int(level) if level.isdigit() else 0, 8)
        fmt = formats.get((num_id, str(ilvl)), "decimal" if num_id == "list number" else "bullet")
        c = counters.setdefault(num_id, [0] * 9)
        c[ilvl] += 1
        c[ilvl + 1:] = [0] * (8 - ilvl)
        marker = "-" if fmt in ("bullet", "none") else f"{c[ilvl]}."
        blocks.append(("   " * ilvl + marker + " " + text.replace("\n", "\n" + "   " * (ilvl + 1)), "list " + num_id))
    return _join_blocks(blocks)


def _val(el, child: str, default: str) -> str:
    """w:val of el's child element (e.g. <w:pStyle w:val="Heading1"/>)."""
    c = el.find(_W + child) if el is not None else None
    return c.get(_W + "val", default) if c is not None else default


def _flatten_sdt(elements):
    """Expands content controls (w:sdt) into their block content, keeping document order."""
    out, stack = [], list(reversed(elements))
    while stack:
        el = stack.pop()
        content = el.find(_W + "sdtContent") if el.tag == _W + "sdt" else None
        if content is not None:
            stack.extend(reversed(list(content)))
        else:
            out.append(el)
    return out


def _docx_cell(tc, rels) -> str:
    return " ".join(_docx_inline(p, rels) for p in tc.iter(_W + "p"))


def _docx_inline(container, rels, depth: int = 0) -> str:
    segments: list[list] = []  # [text, bold, italic]; links are pre-rendered with bold=italic=None

    def add(text, b, i):
        if segments and segments[-1][1:] == [b, i] and b is not None:
            segments[-1][0] += text
        else:
            segments.append([text, b, i])

    for el in container:
        if el.tag == _W + "r":
            rpr = el.find(_W + "rPr")
            b = _on(rpr, "b")
            i = _on(rpr, "i")
            for t in el:
                if t.tag == _W + "t":
                    add(t.text or "", b, i)
                elif t.tag == _W + "tab":
                    add("\t", b, i)
                elif t.tag in (_W + "br", _W + "cr"):
                    add("\n", b, i)
        elif el.tag == _W + "hyperlink" and depth < 32:
            text = _docx_inline(el, rels, depth + 1)
            url = rels.get(el.get(_R + "id"), "") if el.get(_R + "id") else ("#" + el.get(_W + "anchor") if el.get(_W + "anchor") else "")
            add(f"[{text}]({url})" if url and text.strip() else text, None, None)
        elif el.tag in (_W + "ins", _W + "smartTag", _W + "fldSimple", _W + "customXml") and depth < 32:
            add(_docx_inline(el, rels, depth + 1), None, None)
        elif el.tag == _W + "sdt" and depth < 32:
            content = el.find(_W + "sdtContent")
            if content is not None:
                add(_docx_inline(content, rels, depth + 1), None, None)
    out = []
    for text, b, i in segments:
        if i:
            text = _wrap(text, "*")
        if b:
            text = _wrap(text, "**")
        out.append(text)
    return "".join(out)


def _on(rpr, prop: str) -> bool:
    el = rpr.find(_W + prop) if rpr is not None else None
    return el is not None and el.get(_W + "val", "true").lower() not in ("0", "false", "off", "none")


# ---------- xlsx ----------

def _xlsx(data: bytes) -> str:
    pkg = _Package(data)
    wb = pkg.xml("xl/workbook.xml")
    if wb is None:
        raise ShellError(spec_message("to-markdown", "partMissing", format="xlsx", part="xl/workbook.xml"))
    rels = pkg.rels("xl/workbook.xml")
    shared = []
    sst = pkg.xml("xl/sharedStrings.xml")
    for si in (sst if sst is not None else []):
        phonetic = {t for rph in si.iter(_S + "rPh") for t in rph.iter(_S + "t")}
        shared.append("".join(t.text or "" for t in si.iter(_S + "t") if t not in phonetic))
    parts = []
    for sheet in wb.iter(_S + "sheet"):
        root = pkg.xml(rels.get(sheet.get(_R + "id"), ""))
        rows = []
        for row in (root.iter(_S + "row") if root is not None else []):
            cells: dict[int, str] = {}
            col = -1
            for c in row.findall(_S + "c"):
                col += 1
                ref = re.match(r"([A-Z]+)", c.get("r", ""))
                if ref and len(ref.group(1)) > 3:  # past Excel's last column
                    continue
                if ref:
                    col = 0
                    for ch in ref.group(1):
                        col = col * 26 + ord(ch) - 64
                    col -= 1
                value = _xlsx_value(c, shared)
                if value:
                    cells[col] = value
            if cells:
                rows.append(cells)
        first = min((min(r) for r in rows), default=0)
        last = max((max(r) for r in rows), default=-1)
        if (last - first + 1) * len(rows) > MAX_SHEET_CELLS:
            raise ShellError(spec_message("to-markdown", "sheetTooLarge", name=str(sheet.get('name'))))
        table = _table([[r.get(k, "") for k in range(first, last + 1)] for r in rows])
        parts.append(f"## {sheet.get('name', '')}" + ("\n\n" + table if table else ""))
    return "\n\n".join(parts) + "\n" if parts else ""


def _xlsx_value(c, shared: list[str]) -> str:
    t = c.get("t", "n")
    v = c.find(_S + "v")
    v = v.text or "" if v is not None else None
    if t == "inlineStr":
        return "".join(x.text or "" for x in c.iter(_S + "t"))
    if v is None:  # formula without a cached value
        f = c.find(_S + "f")
        return "=" + f.text if f is not None and f.text else ""
    if t == "s":
        return shared[int(v)] if v.isdigit() and int(v) < len(shared) else ""
    if t == "b":
        return "TRUE" if v == "1" else "FALSE"
    return v


# ---------- pptx ----------

def _pptx(data: bytes) -> str:
    pkg = _Package(data)
    pres = pkg.xml("ppt/presentation.xml")
    if pres is None:
        raise ShellError(spec_message("to-markdown", "partMissing", format="pptx", part="ppt/presentation.xml"))
    rels = pkg.rels("ppt/presentation.xml")
    parts = []
    for n, sld in enumerate(pres.iter(_P + "sldId"), 1):
        name = rels.get(sld.get(_R + "id"), "")
        slide = pkg.xml(name)
        if slide is None:
            continue
        title, blocks = _pptx_shapes(slide, notes=False)
        out = [f"## {title or f'Slide {n}'}"] + ([_join_blocks(blocks).rstrip("\n")] if blocks else [])
        notes_part = next((t for t in pkg.rels(name).values() if t.endswith(".xml") and "notesSlide" in t), None)
        notes = pkg.xml(notes_part) if notes_part else None
        if notes is not None:
            _, note_blocks = _pptx_shapes(notes, notes=True)
            if note_blocks:
                out.append("### Notes\n\n" + _join_blocks(note_blocks).rstrip("\n"))
        parts.append("\n\n".join(out))
    return "\n\n".join(parts) + "\n" if parts else ""


def _pptx_shapes(slide, notes: bool):
    """Returns (title text or None, blocks) for a slide or a notes page, skipping slide numbers, footers and dates."""
    title, blocks = None, []
    for el in slide.iter():
        if el.tag == _A + "tbl":
            blocks.append((_table([[" ".join(_pptx_text(p) for p in tc.iter(_A + "p")) for tc in tr.iter(_A + "tc")]
                                   for tr in el.iter(_A + "tr")]), False))
            continue
        if el.tag != _P + "sp":
            continue
        ph = el.find(f"{_P}nvSpPr/{_P}nvPr/{_P}ph")
        kind = ph.get("type", "body") if ph is not None else None
        body = el.find(_P + "txBody")
        if body is None or kind in ("sldNum", "ftr", "dt", "hdr", "sldImg"):
            continue
        paras = [(p, _pptx_text(p)) for p in body.findall(_A + "p")]
        if kind in ("title", "ctrTitle") and title is None and not notes:
            title = " ".join(" ".join(t for _, t in paras).split())
            continue
        for p, text in paras:
            if not text.strip():
                continue
            ppr = p.find(_A + "pPr")
            level = min(int(ppr.get("lvl", "0")) if ppr is not None and ppr.get("lvl", "0").isdigit() else 0, 8)
            explicit = ppr is not None and (ppr.find(_A + "buChar") is not None or ppr.find(_A + "buAutoNum") is not None)
            off = ppr is not None and ppr.find(_A + "buNone") is not None
            bullet = not notes and not off and (explicit or kind in ("body", "obj"))
            blocks.append(("   " * level + "- " + text if bullet else text, bullet))
    return title, blocks


def _pptx_text(p) -> str:
    out = []
    for el in p:
        if el.tag in (_A + "r", _A + "fld"):
            t = el.find(_A + "t")
            out.append(t.text or "" if t is not None else "")
        elif el.tag == _A + "br":
            out.append("\n")
    return "".join(out)


# ---------- html ----------

_SKIP = {"script", "style", "noscript", "template", "head", "nav", "svg", "iframe"}
_BLOCK = {"p", "div", "section", "article", "main", "header", "footer", "aside", "blockquote", "figure", "figcaption",
          "dl", "dt", "dd", "address", "form", "fieldset", "details", "summary", "body", "html", "tr", "table", "caption"}
_BR = "\x00"  # placeholder for <br> that survives whitespace collapsing


class _Html(HTMLParser):
    """Streams tags into Markdown without recursion (so deeply nested input can't overflow the stack)."""

    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.blocks: list[tuple[str, bool]] = []
        self.buf: list[str] = []
        self.inline: list[tuple[str, int, str]] = []  # open a/strong/em/code: (tag, buf position, href)
        self.lists: list[list] = []  # [tag, counter]
        self.list_id = 0  # numbers top-level lists so two adjacent lists don't run together
        self.prefix: str | None = None  # list marker waiting for the current <li>'s first text
        self.skip = 0
        self.pre: list[str] | None = None
        self.table: list[list[str]] | None = None
        self.table_depth = 0

    def flush(self, heading: int = 0) -> None:
        text = re.sub(r"[ \t\r\n\f\v]+", " ", "".join(self.buf)).strip(" ")
        text = "\n".join(line.strip() for line in text.split(_BR)).strip("\n")
        self.buf = []
        if not text:
            return
        if heading:
            self.blocks.append(("#" * heading + " " + " ".join(text.split()), False))
        elif self.lists:
            indent = "   " * len(self.lists)
            first, self.prefix = self.prefix, None
            text = text.replace("\n", "\n" + indent)
            self.blocks.append(((first or indent) + text, self.list_id))
        else:
            self.blocks.append((text, False))

    def handle_starttag(self, tag, attrs):
        a = dict(attrs)
        if tag in _SKIP:
            self.skip += 1
            return
        if self.skip:
            return
        if self.pre is not None:
            if tag == "br":
                self.pre.append("\n")
            return
        if self.table is not None:
            if tag == "table":
                self.table_depth += 1
            elif self.table_depth == 0 and tag == "tr":
                self.table.append([])
            elif self.table_depth == 0 and tag in ("td", "th"):
                self.buf, self.inline = [], []
            elif tag == "br":
                self.buf.append(" ")
            elif tag == "img":
                self.buf.append(f"![{a.get('alt') or ''}]({a.get('src') or ''})")
            elif tag in ("a", "strong", "b", "em", "i", "code"):
                self.inline.append((tag, len(self.buf), a.get("href") or ""))
            return
        if tag in ("h1", "h2", "h3", "h4", "h5", "h6", "pre", "table", "hr") or tag in _BLOCK:
            self.flush()
        if tag == "pre":
            self.pre = []
        elif tag == "table":
            self.table = []
        elif tag == "hr":
            self.blocks.append(("---", False))
        elif tag in ("ul", "ol"):
            self.flush()
            self.list_id += not self.lists
            start = a.get("start") or "1"
            self.lists.append([tag, int(start) - 1 if start.isdigit() else 0])
        elif tag == "li":
            self.flush()
            if self.lists:
                kind = self.lists[-1]
                kind[1] += 1
                self.prefix = "   " * (len(self.lists) - 1) + ("-" if kind[0] == "ul" else f"{kind[1]}.") + " "
        elif tag == "br":
            self.buf.append(_BR)
        elif tag == "img":
            self.buf.append(f"![{a.get('alt') or ''}]({a.get('src') or ''})")
        elif tag in ("a", "strong", "b", "em", "i", "code"):
            self.inline.append((tag, len(self.buf), a.get("href") or ""))

    def handle_endtag(self, tag):
        if tag in _SKIP:
            self.skip = max(0, self.skip - 1)
            return
        if self.skip:
            return
        if self.pre is not None:
            if tag == "pre":
                code = "".join(self.pre).strip("\n")
                self.pre = None
                self.blocks.append(("```\n" + code + "\n```", False))
            return
        if self.table is not None:
            if tag == "table" and self.table_depth:
                self.table_depth -= 1
            elif tag == "table":
                rows, self.table = [r for r in self.table if r], None
                if rows:
                    self.blocks.append((_table(rows), False))
            elif self.table_depth == 0 and tag in ("td", "th"):
                if not self.table:
                    self.table.append([])
                self.table[-1].append("".join(self.buf))
                self.buf = []
            if tag in ("table", "tr", "td", "th"):
                return
        if self.table is None and tag in ("h1", "h2", "h3", "h4", "h5", "h6"):
            self.flush(int(tag[1]))
        elif self.table is None and tag in ("ul", "ol"):
            self.flush()
            if self.lists:
                self.lists.pop()
        elif self.table is None and (tag == "li" or tag in _BLOCK):
            self.flush()
        else:
            for k in range(len(self.inline) - 1, -1, -1):
                open_tag, pos, href = self.inline[k]
                if open_tag != tag:
                    continue
                del self.inline[k:]
                text = "".join(self.buf[pos:])
                if tag == "a":
                    wrapped = f"[{text.strip()}]({href})" if href and text.strip() and not href.startswith("javascript:") else text
                elif tag == "code":
                    wrapped = f"`{text}`" if text.strip() else text
                else:
                    wrapped = _wrap(text, "**" if tag in ("strong", "b") else "*")
                self.buf[pos:] = [wrapped]
                break

    def handle_data(self, data):
        if self.skip:
            return
        (self.pre if self.pre is not None else self.buf).append(data)


def _html(data: bytes) -> str:
    p = _Html()
    p.feed(data.decode("utf-8", "replace"))
    p.close()
    if p.pre is not None:
        p.handle_endtag("pre")
    if p.table is not None:
        p.table_depth = 0
        p.handle_endtag("table")
    p.flush()
    return _join_blocks(p.blocks)


# ---------- plain formats ----------

def _delimited(delimiter: str):
    def convert(data: bytes) -> str:
        rows = [r for r in csv.reader(io.StringIO(data.decode("utf-8-sig", "replace"), newline=""), delimiter=delimiter) if r]
        return _table(rows) + "\n" if rows else ""
    return convert


def _json(data: bytes) -> str:
    try:
        value = json.loads(data.decode("utf-8-sig"))
    except ValueError as e:
        raise ShellError(spec_message("to-markdown", "invalidFile", format="json", detail=str(e))) from None
    return "```json\n" + json.dumps(value, indent=2, ensure_ascii=False) + "\n```\n"


def _pdf(data: bytes) -> str:
    # no in-process PDF parser; running a native one on agent-supplied bytes would reach the host
    raise ShellError("PDF is not supported by to-markdown; if a `pdftotext` program is listed in `help`, run: pdftotext -layout FILE.pdf -")


_CONVERTERS = {
    ".docx": _docx, ".xlsx": _xlsx, ".pptx": _pptx, ".html": _html, ".pdf": _pdf,
    ".csv": _delimited(","), ".tsv": _delimited("\t"), ".json": _json,
    ".md": lambda d: d.decode("utf-8", "replace"), ".txt": lambda d: d.decode("utf-8", "replace"),
}
