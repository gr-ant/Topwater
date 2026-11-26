using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Topwater;

/// <summary>
/// Document-to-Markdown conversion (docx, xlsx, pptx, html, csv/tsv, json, md/txt) with no dependencies, all in-process.
/// PDF is not converted: that needs a native parser, so a host that wants it attaches the real program
/// (<c>shell.RegisterHostCommand("pdftotext")</c>). Malformed input throws <see cref="ShellException"/>, never anything else.
/// Mirrors python/src/topwater/convert.py.
/// </summary>
public static class MarkdownConverter
{
    /// <summary>Zip-bomb guard: total bytes read out of one archive.</summary>
    public static long MaxUncompressedBytes { get; set; } = 100L * 1024 * 1024;
    const string Supported = ".docx, .xlsx, .pptx, .html, .htm, .csv, .tsv, .json, .md, .txt";

    static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    static readonly Dictionary<string, Func<byte[], string>> Converters = new()
    {
        [".docx"] = d => Docx(new Package(d)), [".xlsx"] = d => Xlsx(new Package(d)), [".pptx"] = d => Pptx(new Package(d)),
        [".html"] = Html, [".pdf"] = Pdf, [".csv"] = d => Delimited(d, ','), [".tsv"] = d => Delimited(d, '\t'), [".json"] = Json,
        [".md"] = Encoding.UTF8.GetString, [".txt"] = Encoding.UTF8.GetString,
    };

    /// <summary>Converts a document to Markdown. The format comes from the file name's extension, falling back to magic bytes.</summary>
    public static string ToMarkdown(byte[] data, string fileName)
    {
        var ext = Path.GetExtension(fileName.ToLowerInvariant());
        var fmt = ext switch { ".htm" or ".xhtml" => ".html", ".markdown" => ".md", _ => ext };
        if (!Converters.ContainsKey(fmt)) fmt = Sniff(data) ?? throw new ShellException(CommandSpec.Message("to-markdown", "unsupportedFormat", ("format", ext.Length > 0 ? ext : fileName), ("supported", Supported)));
        try { return Converters[fmt](data); }
        catch (Exception e) when (e is not ShellException) // corrupt zip/XML/encoding: report, never crash the host
        {
            throw new ShellException(CommandSpec.Message("to-markdown", "invalidFile", ("format", fmt[1..]), ("detail", e.Message)));
        }
    }

    /// <summary><c>file</c>-style description of common binary formats, or null if data isn't one of them.</summary>
    public static string? FileType(byte[] data)
    {
        if (StartsWith(data, "%PDF-")) return $"PDF document, version {Encoding.Latin1.GetString(data, 5, Math.Min(3, data.Length - 5))}";
        if (StartsWith(data, "\x89PNG\r\n\x1a\n") && data.Length >= 29)
        {
            var kind = data[25] switch { 0 => " grayscale", 2 => "/color RGB", 3 => " colormap", 4 => " gray+alpha", 6 => "/color RGBA", _ => "" };
            return $"PNG image data, {BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(16))} x {BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(20))}, " +
                   $"{data[24]}-bit{kind}, {(data[28] != 0 ? "interlaced" : "non-interlaced")}";
        }
        if (StartsWith(data, "\xff\xd8\xff")) return "JPEG image data";
        if (StartsWith(data, "SQLite format 3\0")) return "SQLite 3.x database";
        if (StartsWith(data, "\x1f\x8b")) return "gzip compressed data";
        if (StartsWith(data, "PK\x03\x04") || StartsWith(data, "PK\x05\x06"))
            return Sniff(data) switch { ".docx" => "Microsoft Word 2007+", ".xlsx" => "Microsoft Excel 2007+", ".pptx" => "Microsoft PowerPoint 2007+", _ => "Zip archive data" };
        return null;
    }

    static bool StartsWith(byte[] data, string latin1) => data.AsSpan().StartsWith(Encoding.Latin1.GetBytes(latin1));

    static string? Sniff(byte[] data)
    {
        if (StartsWith(data, "%PDF-")) return ".pdf";
        if (StartsWith(data, "PK"))
        {
            HashSet<string> names;
            try { using var zip = new ZipArchive(new MemoryStream(data)); names = zip.Entries.Select(e => e.FullName).ToHashSet(); }
            catch (Exception e) when (e is InvalidDataException or IOException or ArgumentException) { return null; }
            foreach (var (part, fmt) in new[] { ("word/document.xml", ".docx"), ("xl/workbook.xml", ".xlsx"), ("ppt/presentation.xml", ".pptx") })
                if (names.Contains(part)) return fmt;
            return null;
        }
        var head = Encoding.Latin1.GetString(data, 0, Math.Min(1024, data.Length)).TrimStart().ToLowerInvariant();
        if (head.StartsWith("<!doctype html") || head.StartsWith("<html")) return ".html";
        if (head.StartsWith('{') || head.StartsWith('['))
        {
            try { using var _ = JsonDocument.Parse(data); return ".json"; }
            catch (JsonException) { }
        }
        return null;
    }

    // ---------- shared helpers ----------

    /// <summary>A zip (OOXML package) read with a cap on total uncompressed bytes and no DTDs/entities in XML parts.</summary>
    sealed class Package(byte[] data)
    {
        readonly ZipArchive _zip = new(new MemoryStream(data));
        long _budget = MaxUncompressedBytes;

        public byte[]? Read(string name)
        {
            if (name.Length == 0 || _zip.GetEntry(name) is not { } entry) return null;
            using var s = entry.Open();
            var buf = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = s.Read(chunk, 0, chunk.Length)) > 0)
            {
                buf.Write(chunk, 0, n);
                if (buf.Length > _budget) throw new ShellException(CommandSpec.Message("to-markdown", "archiveTooLarge", ("megabytes", (MaxUncompressedBytes / (1024 * 1024)).ToString())));
            }
            _budget -= buf.Length;
            return buf.ToArray();
        }

        public XElement? Xml(string name)
        {
            var bytes = Read(name);
            if (bytes == null) return null;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            try { using var reader = XmlReader.Create(new MemoryStream(bytes), settings); return XDocument.Load(reader).Root; }
            catch (XmlException e) when (e.Message.Contains("DTD")) { throw new ShellException(CommandSpec.Message("to-markdown", "dtdNotAllowed", ("name", name))); }
        }

        /// <summary>Relationship id -> absolute part name (or external URL) for a part.</summary>
        public Dictionary<string, string> Rels(string part)
        {
            int slash = part.LastIndexOf('/');
            string dir = slash < 0 ? "" : part[..slash], name = part[(slash + 1)..];
            var result = new Dictionary<string, string>();
            if (Xml((dir.Length > 0 ? dir + "/" : "") + "_rels/" + name + ".rels") is not { } root) return result;
            foreach (var r in root.Elements())
            {
                var target = (string?)r.Attribute("Target") ?? "";
                if ((string?)r.Attribute("TargetMode") != "External")
                    target = target.StartsWith('/') ? target[1..] : NormPath(dir.Length > 0 ? dir + "/" + target : target);
                result[(string?)r.Attribute("Id") ?? ""] = target;
            }
            return result;
        }

        static string NormPath(string path)
        {
            var stack = new List<string>();
            foreach (var seg in path.Split('/'))
            {
                if (seg is "" or ".") continue;
                if (seg == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); }
                else stack.Add(seg);
            }
            return string.Join('/', stack);
        }
    }

    static string Squash(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Pipe table; the first row is the header.</summary>
    static string Table(List<List<string>> rows)
    {
        var cells = rows.Select(r => r.Select(c => Squash(c).Replace("|", "\\|")).ToList()).ToList();
        int width = cells.Count == 0 ? 0 : cells.Max(r => r.Count);
        if (width == 0) return "";
        var lines = cells.Select(r => "| " + string.Join(" | ", r.Concat(Enumerable.Repeat("", width - r.Count))) + " |").ToList();
        lines.Insert(1, "|" + string.Concat(Enumerable.Repeat(" --- |", width)));
        return string.Join('\n', lines);
    }

    /// <summary>Wraps text in an emphasis marker, keeping surrounding whitespace outside it.</summary>
    static string Wrap(string text, string marker)
    {
        var core = text.Trim();
        if (core.Length == 0) return text;
        int start = text.Length - text.TrimStart().Length;
        return text[..start] + marker + core + marker + text[(start + core.Length)..];
    }

    /// <summary>Blocks are (markdown, list id or null); consecutive items of one list stay tight, all else gets a blank line.</summary>
    static string JoinBlocks(List<(string Text, string? List)> blocks)
    {
        var kept = blocks.Where(b => b.Text.Length > 0).ToList();
        if (kept.Count == 0) return "";
        var sb = new StringBuilder();
        for (int i = 0; i < kept.Count; i++)
        {
            if (i > 0) sb.Append(kept[i].List != null && kept[i].List == kept[i - 1].List ? "\n" : "\n\n");
            sb.Append(kept[i].Text);
        }
        return sb.ToString().Trim('\n') + "\n";
    }

    static int Level(string? s) => int.TryParse(s, out var n) && n >= 0 ? Math.Min(n, 8) : 0;

    // ---------- docx ----------

    static string Docx(Package pkg)
    {
        var doc = pkg.Xml("word/document.xml") ?? throw new ShellException(CommandSpec.Message("to-markdown", "partMissing", ("format", "docx"), ("part", "word/document.xml")));
        var rels = pkg.Rels("word/document.xml");
        var styles = new Dictionary<string, (string Name, (string NumId, string Ilvl)? Num)>();
        foreach (var s in pkg.Xml("word/styles.xml")?.Elements(W + "style") ?? [])
        {
            var num = s.Element(W + "pPr")?.Element(W + "numPr");
            styles[(string?)s.Attribute(W + "styleId") ?? ""] = (((string?)s.Element(W + "name")?.Attribute(W + "val") ?? "").ToLowerInvariant(),
                num == null ? null : (Val(num, "numId", "0"), Val(num, "ilvl", "0")));
        }
        var formats = new Dictionary<(string, string), string>(); // (numId, ilvl) -> numFmt
        if (pkg.Xml("word/numbering.xml") is { } numbering)
        {
            var abstracts = new Dictionary<string, XElement>();
            foreach (var a in numbering.Elements(W + "abstractNum")) abstracts[(string?)a.Attribute(W + "abstractNumId") ?? ""] = a;
            foreach (var n in numbering.Elements(W + "num"))
                if (abstracts.TryGetValue(Val(n, "abstractNumId", ""), out var a))
                    foreach (var lvl in a.Elements(W + "lvl"))
                        formats[((string?)n.Attribute(W + "numId") ?? "", (string?)lvl.Attribute(W + "ilvl") ?? "")] = Val(lvl, "numFmt", "bullet");
        }

        var counters = new Dictionary<string, int[]>();
        var blocks = new List<(string, string?)>();
        foreach (var el in FlattenSdt(doc.Element(W + "body")?.Elements() ?? []))
        {
            if (el.Name == W + "tbl")
            {
                blocks.Add((Table(el.Descendants(W + "tr").Select(tr => tr.Descendants(W + "tc").Select(tc => DocxCell(tc, rels)).ToList()).ToList()), null));
                continue;
            }
            if (el.Name != W + "p") continue;
            var text = DocxInline(el, rels, 0).Trim();
            if (text.Length == 0) continue;
            var styleId = Val(el.Element(W + "pPr"), "pStyle", "");
            var (styleName, styleNum) = styles.GetValueOrDefault(styleId, ("", null));
            var label = (styleId + " " + styleName).ToLowerInvariant();
            var heading = Regex.Match(label, @"heading\s*([1-6])");
            if (heading.Success || (Regex.IsMatch(label, @"\btitle\b") && !label.Contains("subtitle")))
            {
                blocks.Add((new string('#', heading.Success ? int.Parse(heading.Groups[1].Value) : 1) + " " + Squash(text), null));
                continue;
            }
            var numPr = el.Element(W + "pPr")?.Element(W + "numPr");
            var (numId, level) = numPr != null ? (Val(numPr, "numId", "0"), Val(numPr, "ilvl", "0")) : styleNum ?? ("0", "0");
            if (numId == "0" && (styleName.Contains("list bullet") || styleName.Contains("list number")))
                numId = styleName.Contains("list number") ? "list number" : "list bullet";
            if (numId == "0") { blocks.Add((text, null)); continue; }
            int ilvl = Level(level);
            var fmt = formats.GetValueOrDefault((numId, ilvl.ToString()), numId == "list number" ? "decimal" : "bullet");
            if (!counters.TryGetValue(numId, out var c)) counters[numId] = c = new int[9];
            c[ilvl]++;
            for (int k = ilvl + 1; k < 9; k++) c[k] = 0;
            var marker = fmt is "bullet" or "none" ? "-" : $"{c[ilvl]}.";
            blocks.Add((Indent(ilvl) + marker + " " + text.Replace("\n", "\n" + Indent(ilvl + 1)), "list " + numId));
        }
        return JoinBlocks(blocks);
    }

    static string Indent(int level) => new(' ', 3 * level);

    /// <summary>w:val of el's child element (e.g. &lt;w:pStyle w:val="Heading1"/&gt;).</summary>
    static string Val(XElement? el, string child, string fallback) =>
        el?.Element(W + child) is { } c ? (string?)c.Attribute(W + "val") ?? fallback : fallback;

    /// <summary>Expands content controls (w:sdt) into their block content, keeping document order.</summary>
    static List<XElement> FlattenSdt(IEnumerable<XElement> elements)
    {
        var result = new List<XElement>();
        var stack = new Stack<XElement>(elements.Reverse());
        while (stack.Count > 0)
        {
            var el = stack.Pop();
            var content = el.Name == W + "sdt" ? el.Element(W + "sdtContent") : null;
            if (content != null) foreach (var c in content.Elements().Reverse()) stack.Push(c);
            else result.Add(el);
        }
        return result;
    }

    static string DocxCell(XElement tc, Dictionary<string, string> rels) => string.Join(' ', tc.Descendants(W + "p").Select(p => DocxInline(p, rels, 0)));

    static string DocxInline(XElement container, Dictionary<string, string> rels, int depth)
    {
        var segments = new List<(string Text, bool? Bold, bool? Italic)>(); // links etc. are pre-rendered with null formatting
        void Add(string text, bool? b, bool? i)
        {
            if (segments.Count > 0 && b != null && segments[^1].Bold == b && segments[^1].Italic == i)
                segments[^1] = (segments[^1].Text + text, b, i);
            else segments.Add((text, b, i));
        }
        foreach (var el in container.Elements())
        {
            if (el.Name == W + "r")
            {
                var rpr = el.Element(W + "rPr");
                bool b = On(rpr, "b"), i = On(rpr, "i");
                foreach (var t in el.Elements())
                {
                    if (t.Name == W + "t") Add(t.Value, b, i);
                    else if (t.Name == W + "tab") Add("\t", b, i);
                    else if (t.Name == W + "br" || t.Name == W + "cr") Add("\n", b, i);
                }
            }
            else if (el.Name == W + "hyperlink" && depth < 32)
            {
                var text = DocxInline(el, rels, depth + 1);
                var id = (string?)el.Attribute(R + "id");
                var anchor = (string?)el.Attribute(W + "anchor");
                var url = id != null ? rels.GetValueOrDefault(id, "") : anchor != null ? "#" + anchor : "";
                Add(url.Length > 0 && text.Trim().Length > 0 ? $"[{text}]({url})" : text, null, null);
            }
            else if ((el.Name == W + "ins" || el.Name == W + "smartTag" || el.Name == W + "fldSimple" || el.Name == W + "customXml") && depth < 32)
                Add(DocxInline(el, rels, depth + 1), null, null);
            else if (el.Name == W + "sdt" && depth < 32 && el.Element(W + "sdtContent") is { } content)
                Add(DocxInline(content, rels, depth + 1), null, null);
        }
        var sb = new StringBuilder();
        foreach (var (text, b, i) in segments)
        {
            var t = i == true ? Wrap(text, "*") : text;
            sb.Append(b == true ? Wrap(t, "**") : t);
        }
        return sb.ToString();
    }

    static bool On(XElement? rpr, string prop) =>
        rpr?.Element(W + prop) is { } el && ((string?)el.Attribute(W + "val") ?? "true").ToLowerInvariant() is not ("0" or "false" or "off" or "none");

    // ---------- xlsx ----------

    const long MaxSheetCells = 5_000_000;

    static string Xlsx(Package pkg)
    {
        var wb = pkg.Xml("xl/workbook.xml") ?? throw new ShellException(CommandSpec.Message("to-markdown", "partMissing", ("format", "xlsx"), ("part", "xl/workbook.xml")));
        var rels = pkg.Rels("xl/workbook.xml");
        var shared = new List<string>();
        foreach (var si in pkg.Xml("xl/sharedStrings.xml")?.Elements() ?? [])
        {
            var phonetic = si.Descendants(S + "rPh").SelectMany(r => r.Descendants(S + "t")).ToHashSet();
            shared.Add(string.Concat(si.Descendants(S + "t").Where(t => !phonetic.Contains(t)).Select(t => t.Value)));
        }
        var parts = new List<string>();
        foreach (var sheet in wb.Descendants(S + "sheet"))
        {
            var root = pkg.Xml(rels.GetValueOrDefault((string?)sheet.Attribute(R + "id") ?? "", ""));
            var rows = new List<Dictionary<int, string>>();
            foreach (var row in root?.Descendants(S + "row") ?? [])
            {
                var cells = new Dictionary<int, string>();
                int col = -1;
                foreach (var c in row.Elements(S + "c"))
                {
                    col++;
                    var m = Regex.Match((string?)c.Attribute("r") ?? "", "^([A-Z]+)");
                    if (m.Success && m.Length > 3) continue; // past Excel's last column
                    if (m.Success) col = m.Value.Aggregate(0, (n, ch) => n * 26 + ch - 64) - 1;
                    var value = XlsxValue(c, shared);
                    if (value.Length > 0) cells[col] = value;
                }
                if (cells.Count > 0) rows.Add(cells);
            }
            int first = rows.Count == 0 ? 0 : rows.Min(r => r.Keys.Min()), last = rows.Count == 0 ? -1 : rows.Max(r => r.Keys.Max());
            if ((long)(last - first + 1) * rows.Count > MaxSheetCells) throw new ShellException(CommandSpec.Message("to-markdown", "sheetTooLarge", ("name", (string?)sheet.Attribute("name") ?? "")));
            var table = Table(rows.Select(r => Enumerable.Range(first, last - first + 1).Select(k => r.GetValueOrDefault(k, "")).ToList()).ToList());
            parts.Add($"## {(string?)sheet.Attribute("name") ?? ""}" + (table.Length > 0 ? "\n\n" + table : ""));
        }
        return parts.Count > 0 ? string.Join("\n\n", parts) + "\n" : "";
    }

    static string XlsxValue(XElement c, List<string> shared)
    {
        var t = (string?)c.Attribute("t") ?? "n";
        var v = c.Element(S + "v")?.Value;
        if (t == "inlineStr") return string.Concat(c.Descendants(S + "t").Select(x => x.Value));
        if (v == null) return c.Element(S + "f") is { Value.Length: > 0 } f ? "=" + f.Value : ""; // formula without a cached value
        if (t == "s") return int.TryParse(v, out var i) && i >= 0 && i < shared.Count ? shared[i] : "";
        if (t == "b") return v == "1" ? "TRUE" : "FALSE";
        return v;
    }

    // ---------- pptx ----------

    static string Pptx(Package pkg)
    {
        var pres = pkg.Xml("ppt/presentation.xml") ?? throw new ShellException(CommandSpec.Message("to-markdown", "partMissing", ("format", "pptx"), ("part", "ppt/presentation.xml")));
        var rels = pkg.Rels("ppt/presentation.xml");
        var parts = new List<string>();
        int n = 0;
        foreach (var sld in pres.Descendants(P + "sldId"))
        {
            n++;
            var name = rels.GetValueOrDefault((string?)sld.Attribute(R + "id") ?? "", "");
            if (pkg.Xml(name) is not { } slide) continue;
            var (title, blocks) = PptxShapes(slide, notes: false);
            var output = new List<string> { $"## {title ?? $"Slide {n}"}" };
            if (blocks.Count > 0) output.Add(JoinBlocks(blocks).TrimEnd('\n'));
            var notesPart = pkg.Rels(name).Values.FirstOrDefault(t => t.EndsWith(".xml") && t.Contains("notesSlide"));
            if (notesPart != null && pkg.Xml(notesPart) is { } notes)
            {
                var (_, noteBlocks) = PptxShapes(notes, notes: true);
                if (noteBlocks.Count > 0) output.Add("### Notes\n\n" + JoinBlocks(noteBlocks).TrimEnd('\n'));
            }
            parts.Add(string.Join("\n\n", output));
        }
        return parts.Count > 0 ? string.Join("\n\n", parts) + "\n" : "";
    }

    /// <summary>Returns (title text or null, blocks) for a slide or a notes page, skipping slide numbers, footers and dates.</summary>
    static (string? Title, List<(string, string?)> Blocks) PptxShapes(XElement slide, bool notes)
    {
        string? title = null;
        var blocks = new List<(string, string?)>();
        foreach (var el in slide.DescendantsAndSelf())
        {
            if (el.Name == A + "tbl")
            {
                blocks.Add((Table(el.Descendants(A + "tr").Select(tr => tr.Descendants(A + "tc")
                    .Select(tc => string.Join(' ', tc.Descendants(A + "p").Select(PptxText))).ToList()).ToList()), null));
                continue;
            }
            if (el.Name != P + "sp") continue;
            var ph = el.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph");
            var kind = ph == null ? null : (string?)ph.Attribute("type") ?? "body";
            var body = el.Element(P + "txBody");
            if (body == null || kind is "sldNum" or "ftr" or "dt" or "hdr" or "sldImg") continue;
            var paras = body.Elements(A + "p").Select(p => (P: p, Text: PptxText(p))).ToList();
            if (kind is "title" or "ctrTitle" && title == null && !notes)
            {
                title = Squash(string.Join(' ', paras.Select(p => p.Text)));
                continue;
            }
            foreach (var (p, text) in paras)
            {
                if (text.Trim().Length == 0) continue;
                var ppr = p.Element(A + "pPr");
                int level = Level((string?)ppr?.Attribute("lvl"));
                bool explicitBullet = ppr?.Element(A + "buChar") != null || ppr?.Element(A + "buAutoNum") != null;
                bool off = ppr?.Element(A + "buNone") != null;
                bool bullet = !notes && !off && (explicitBullet || kind is "body" or "obj");
                blocks.Add(bullet ? (Indent(level) + "- " + text, "bullet") : (text, null));
            }
        }
        return (title, blocks);
    }

    static string PptxText(XElement p) => string.Concat(p.Elements().Select(el =>
        el.Name == A + "r" || el.Name == A + "fld" ? el.Element(A + "t")?.Value ?? "" : el.Name == A + "br" ? "\n" : ""));

    // ---------- html ----------

    static readonly Regex HtmlToken = new(@"<!--.*?-->|<![^>]*>|<\?[^>]*>|</?([A-Za-z][A-Za-z0-9:-]*)((?:[^>""']|""[^""]*""|'[^']*')*)>|[^<]+|<",
        RegexOptions.Singleline | RegexOptions.NonBacktracking);
    static readonly Regex HtmlAttr = new(@"([^\s=/>""']+)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s>""']+)))?", RegexOptions.NonBacktracking);

    static string Html(byte[] data)
    {
        var text = Encoding.UTF8.GetString(data);
        var h = new HtmlState();
        int pos = 0;
        while (pos < text.Length)
        {
            var m = HtmlToken.Match(text, pos);
            if (!m.Success) break;
            pos = m.Index + m.Length;
            if (m.Groups[1].Success)
            {
                var tag = m.Groups[1].Value.ToLowerInvariant();
                if (m.Value.StartsWith("</")) { h.End(tag); continue; }
                var attrs = new Dictionary<string, string>();
                foreach (Match a in HtmlAttr.Matches(m.Groups[2].Value))
                    attrs[a.Groups[1].Value.ToLowerInvariant()] = WebUtility.HtmlDecode(a.Groups[2].Success ? a.Groups[2].Value : a.Groups[3].Success ? a.Groups[3].Value : a.Groups[4].Value);
                h.Start(tag, attrs);
                if (m.Groups[2].Value.TrimEnd().EndsWith('/')) h.End(tag);
                else if (tag is "script" or "style") // raw text: skip to the closing tag
                {
                    int close = text.IndexOf("</" + tag, pos, StringComparison.OrdinalIgnoreCase);
                    pos = close < 0 ? text.Length : close;
                }
            }
            else if (!m.Value.StartsWith("<!") && !m.Value.StartsWith("<?") || m.Value == "<") h.Data(WebUtility.HtmlDecode(m.Value));
        }
        if (h.PreBuf != null) h.End("pre");
        if (h.TableRows != null) { h.TableDepth = 0; h.End("table"); }
        h.Flush();
        return JoinBlocks(h.Blocks);
    }

    /// <summary>Streams tags into Markdown without recursion (so deeply nested input can't overflow the stack).</summary>
    sealed class HtmlState
    {
        static readonly HashSet<string> Skip = ["script", "style", "noscript", "template", "head", "nav", "svg", "iframe"];
        static readonly HashSet<string> Block = ["p", "div", "section", "article", "main", "header", "footer", "aside", "blockquote", "figure", "figcaption",
            "dl", "dt", "dd", "address", "form", "fieldset", "details", "summary", "body", "html", "tr", "table", "caption"];
        static readonly HashSet<string> Inline = ["a", "strong", "b", "em", "i", "code"];
        const char Br = '\0'; // placeholder for <br> that survives whitespace collapsing

        public readonly List<(string, string?)> Blocks = [];
        List<string> _buf = [];
        List<(string Tag, int Pos, string Href)> _inline = [];
        readonly List<(string Tag, int Count)> _lists = [];
        int _listId; // numbers top-level lists so two adjacent lists don't run together
        string? _prefix; // list marker waiting for the current <li>'s first text
        int _skip;
        public List<string>? PreBuf;
        public List<List<string>>? TableRows;
        public int TableDepth;

        public void Flush(int heading = 0)
        {
            var text = Regex.Replace(string.Concat(_buf), @"[ \t\r\n\f\v]+", " ").Trim(' ');
            text = string.Join('\n', text.Split(Br).Select(l => l.Trim())).Trim('\n');
            _buf = [];
            if (text.Length == 0) return;
            if (heading > 0) Blocks.Add((new string('#', heading) + " " + Squash(text), null));
            else if (_lists.Count > 0)
            {
                var indent = Indent(_lists.Count);
                var first = _prefix;
                _prefix = null;
                Blocks.Add(((first ?? indent) + text.Replace("\n", "\n" + indent), _listId.ToString()));
            }
            else Blocks.Add((text, null));
        }

        static string Image(Dictionary<string, string> a) => $"![{a.GetValueOrDefault("alt", "")}]({a.GetValueOrDefault("src", "")})";

        public void Start(string tag, Dictionary<string, string> a)
        {
            if (Skip.Contains(tag)) { _skip++; return; }
            if (_skip > 0) return;
            if (PreBuf != null) { if (tag == "br") PreBuf.Add("\n"); return; }
            if (TableRows != null)
            {
                if (tag == "table") TableDepth++;
                else if (TableDepth == 0 && tag == "tr") TableRows.Add([]);
                else if (TableDepth == 0 && tag is "td" or "th") { _buf = []; _inline = []; }
                else if (tag == "br") _buf.Add(" ");
                else if (tag == "img") _buf.Add(Image(a));
                else if (Inline.Contains(tag)) _inline.Add((tag, _buf.Count, a.GetValueOrDefault("href", "")));
                return;
            }
            if (tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "pre" or "table" or "hr" || Block.Contains(tag)) Flush();
            if (tag == "pre") PreBuf = [];
            else if (tag == "table") TableRows = [];
            else if (tag == "hr") Blocks.Add(("---", null));
            else if (tag is "ul" or "ol")
            {
                Flush();
                if (_lists.Count == 0) _listId++;
                var start = a.GetValueOrDefault("start", "");
                _lists.Add((tag, start.Length > 0 && start.All(char.IsAsciiDigit) && int.TryParse(start, out var s) ? s - 1 : 0));
            }
            else if (tag == "li")
            {
                Flush();
                if (_lists.Count > 0)
                {
                    var (kind, count) = _lists[^1];
                    _lists[^1] = (kind, ++count);
                    _prefix = Indent(_lists.Count - 1) + (kind == "ul" ? "-" : $"{count}.") + " ";
                }
            }
            else if (tag == "br") _buf.Add(Br.ToString());
            else if (tag == "img") _buf.Add(Image(a));
            else if (Inline.Contains(tag)) _inline.Add((tag, _buf.Count, a.GetValueOrDefault("href", "")));
        }

        public void End(string tag)
        {
            if (Skip.Contains(tag)) { _skip = Math.Max(0, _skip - 1); return; }
            if (_skip > 0) return;
            if (PreBuf != null)
            {
                if (tag == "pre")
                {
                    var code = string.Concat(PreBuf).Trim('\n');
                    PreBuf = null;
                    Blocks.Add(("```\n" + code + "\n```", null));
                }
                return;
            }
            if (TableRows != null)
            {
                if (tag == "table" && TableDepth > 0) TableDepth--;
                else if (tag == "table")
                {
                    var rows = TableRows.Where(r => r.Count > 0).ToList();
                    TableRows = null;
                    if (rows.Count > 0) Blocks.Add((Table(rows), null));
                }
                else if (TableDepth == 0 && tag is "td" or "th")
                {
                    if (TableRows.Count == 0) TableRows.Add([]);
                    TableRows[^1].Add(string.Concat(_buf));
                    _buf = [];
                }
                if (tag is "table" or "tr" or "td" or "th") return;
            }
            if (TableRows == null && tag is "h1" or "h2" or "h3" or "h4" or "h5" or "h6") Flush(tag[1] - '0');
            else if (TableRows == null && tag is "ul" or "ol")
            {
                Flush();
                if (_lists.Count > 0) _lists.RemoveAt(_lists.Count - 1);
            }
            else if (TableRows == null && (tag == "li" || Block.Contains(tag))) Flush();
            else
            {
                for (int k = _inline.Count - 1; k >= 0; k--)
                {
                    var (open, pos, href) = _inline[k];
                    if (open != tag) continue;
                    _inline.RemoveRange(k, _inline.Count - k);
                    pos = Math.Min(pos, _buf.Count);
                    var text = string.Concat(_buf.Skip(pos));
                    var wrapped = tag switch
                    {
                        "a" => href.Length > 0 && text.Trim().Length > 0 && !href.StartsWith("javascript:") ? $"[{text.Trim()}]({href})" : text,
                        "code" => text.Trim().Length > 0 ? $"`{text}`" : text,
                        _ => Wrap(text, tag is "strong" or "b" ? "**" : "*"),
                    };
                    _buf.RemoveRange(pos, _buf.Count - pos);
                    _buf.Add(wrapped);
                    break;
                }
            }
        }

        public void Data(string data)
        {
            if (_skip > 0) return;
            (PreBuf ?? _buf).Add(data);
        }
    }

    // ---------- plain formats ----------

    static string Delimited(byte[] data, char delimiter)
    {
        var text = Encoding.UTF8.GetString(data).TrimStart('﻿');
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, atStart = true, any = false;
        void EndField() { row.Add(field.ToString()); field.Clear(); atStart = true; }
        void EndRow() { if (any) { EndField(); rows.Add(row); } row = []; field.Clear(); any = false; atStart = true; }
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c != '"') field.Append(c);
                else if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else quoted = false;
                continue;
            }
            if (c == '"' && atStart) { quoted = true; atStart = false; any = true; }
            else if (c == delimiter) { EndField(); any = true; }
            else if (c is '\n' or '\r') { if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; EndRow(); }
            else { field.Append(c); atStart = false; any = true; }
        }
        EndRow();
        return rows.Count > 0 ? Table(rows) + "\n" : "";
    }

    static string Json(byte[] data)
    {
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(data).TrimStart('﻿'));
            var opts = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            return "```json\n" + JsonSerializer.Serialize(doc.RootElement, opts) + "\n```\n";
        }
        catch (JsonException e) { throw new ShellException(CommandSpec.Message("to-markdown", "invalidFile", ("format", "json"), ("detail", e.Message))); }
    }

    // no in-process PDF parser; running a native one on agent-supplied bytes would reach the host
    static string Pdf(byte[] data) => throw new ShellException(
        "PDF is not supported by to-markdown; if a `pdftotext` program is listed in `help`, run: pdftotext -layout FILE.pdf -");
}
