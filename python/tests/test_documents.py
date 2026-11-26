"""Binary files in the virtual filesystem, document-to-Markdown conversion and binary inspection commands.
Mirrors tests/Topwater.Tests/DocumentTests.cs."""
import base64
import hashlib
import io
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Shell, ShellError, convert  # noqa: E402
from topwater.convert import to_markdown  # noqa: E402

W = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" ' \
    'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"'
S = 'xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" ' \
    'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"'
P = 'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" ' \
    'xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" ' \
    'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"'
RELS = 'xmlns="http://schemas.openxmlformats.org/package/2006/relationships"'

PNG = bytes.fromhex("89504e470d0a1a0a0000000d4948445200000002000000030806000000") + b"\x00\x01\x02\xff"


def zip_bytes(parts: dict) -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        for name, text in parts.items():
            z.writestr(name, text)
    return buf.getvalue()


def rels(*items) -> str:
    ext = ' TargetMode="External"'
    body = "".join(f'<Relationship Id="{i}" Type="t" Target="{t}"{ext if t.startswith("http") else ""}/>'
                   for i, t in items)
    return f"<Relationships {RELS}>{body}</Relationships>"


def make_docx() -> bytes:
    def p(text, style=None, num=None):
        ppr = (f'<w:pStyle w:val="{style}"/>' if style else "") + (
            f'<w:numPr><w:ilvl w:val="{num[1]}"/><w:numId w:val="{num[0]}"/></w:numPr>' if num else "")
        return f"<w:p><w:pPr>{ppr}</w:pPr><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>"
    body = (
        p("Annual Report", "Title")
        + p("Overview", "Heading1")
        + '<w:p><w:r><w:t xml:space="preserve">Revenue was </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>record</w:t></w:r>'
          '<w:r><w:t xml:space="preserve"> and </w:t></w:r><w:r><w:rPr><w:i/></w:rPr><w:t>growing</w:t></w:r>'
          '<w:r><w:t xml:space="preserve">. See </w:t></w:r><w:hyperlink r:id="rId9"><w:r><w:t>the site</w:t></w:r></w:hyperlink></w:p>'
        + p("Details", "Heading2")
        + p("Apples", num=("1", "0")) + p("Green", num=("1", "1")) + p("Pears", num=("1", "0"))
        + p("First step", num=("2", "0")) + p("Second step", num=("2", "0"))
        + "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Region</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Sales</w:t></w:r></w:p></w:tc></w:tr>"
          "<w:tr><w:tc><w:p><w:r><w:t>North|East</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>100</w:t></w:r></w:p></w:tc></w:tr></w:tbl>"
    )
    numbering = (f"<w:numbering {W}>"
                 '<w:abstractNum w:abstractNumId="10"><w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/></w:lvl><w:lvl w:ilvl="1"><w:numFmt w:val="bullet"/></w:lvl></w:abstractNum>'
                 '<w:abstractNum w:abstractNumId="20"><w:lvl w:ilvl="0"><w:numFmt w:val="decimal"/></w:lvl></w:abstractNum>'
                 '<w:num w:numId="1"><w:abstractNumId w:val="10"/></w:num><w:num w:numId="2"><w:abstractNumId w:val="20"/></w:num></w:numbering>')
    return zip_bytes({
        "[Content_Types].xml": "<Types/>",
        "word/document.xml": f"<w:document {W}><w:body>{body}</w:body></w:document>",
        "word/numbering.xml": numbering,
        "word/_rels/document.xml.rels": rels(("rId9", "https://example.com/")),
    })


DOCX_MD = """# Annual Report

# Overview

Revenue was **record** and *growing*. See [the site](https://example.com/)

## Details

- Apples
   - Green
- Pears

1. First step
2. Second step

| Region | Sales |
| --- | --- |
| North\\|East | 100 |
"""


def make_xlsx() -> bytes:
    sheet1 = (f"<worksheet {S}><sheetData>"
              '<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="inlineStr"><is><t>Ok</t></is></c></row>'
              '<row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2"><v>3.5</v></c><c r="C2" t="b"><v>1</v></c></row>'
              '<row r="4"><c r="A4" t="str"><f>"x"&amp;"y"</f><v>xy</v></c><c r="B4"><f>B2*2</f><v>7</v></c><c r="C4" t="b"><v>0</v></c></row>'
              "</sheetData></worksheet>")
    sheet2 = f'<worksheet {S}><sheetData><row r="1"><c r="B1"><v>42</v></c></row></sheetData></worksheet>'
    return zip_bytes({
        "xl/workbook.xml": f'<workbook {S}><sheets><sheet name="Sales" sheetId="1" r:id="rId1"/><sheet name="Other" sheetId="2" r:id="rId2"/></sheets></workbook>',
        "xl/_rels/workbook.xml.rels": rels(("rId1", "worksheets/sheet1.xml"), ("rId2", "/xl/worksheets/sheet2.xml")),
        "xl/sharedStrings.xml": f"<sst {S}><si><t>Item</t></si><si><t>Price</t></si><si><r><t>Wid</t></r><r><t>get</t></r></si></sst>",
        "xl/worksheets/sheet1.xml": sheet1,
        "xl/worksheets/sheet2.xml": sheet2,
    })


XLSX_MD = """## Sales

| Item | Price | Ok |
| --- | --- | --- |
| Widget | 3.5 | TRUE |
| xy | 7 | FALSE |

## Other

| 42 |
| --- |
"""


def make_pptx() -> bytes:
    def sp(ph, *paras):
        nv = f'<p:nvPr><p:ph type="{ph}"/></p:nvPr>' if ph else "<p:nvPr/>"
        body = "".join("<a:p>" + ('<a:pPr lvl="%s"/>' % lvl if lvl else "") + f"<a:r><a:t>{t}</a:t></a:r></a:p>" for t, lvl in paras)
        return f"<p:sp><p:nvSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvSpPr/>{nv}</p:nvSpPr><p:txBody>{body}</p:txBody></p:sp>"

    def slide(*shapes):
        return f"<p:sld {P}><p:cSld><p:spTree>{''.join(shapes)}</p:spTree></p:cSld></p:sld>"
    return zip_bytes({
        "ppt/presentation.xml": f'<p:presentation {P}><p:sldIdLst><p:sldId id="256" r:id="rId2"/><p:sldId id="257" r:id="rId3"/></p:sldIdLst></p:presentation>',
        "ppt/_rels/presentation.xml.rels": rels(("rId2", "slides/slide1.xml"), ("rId3", "slides/slide2.xml")),
        "ppt/slides/slide1.xml": slide(sp("title", ("Q3 Results", 0)), sp("body", ("Revenue up", 0), ("Mostly EMEA", 1)), sp("sldNum", ("7", 0))),
        "ppt/slides/_rels/slide1.xml.rels": rels(("rId1", "../notesSlides/notesSlide1.xml")),
        "ppt/notesSlides/notesSlide1.xml": slide(sp("sldImg"), sp("body", ("Mention the budget", 0))),
        "ppt/slides/slide2.xml": slide(sp(None, ("Just a text box", 0))),
    })


PPTX_MD = """## Q3 Results

- Revenue up
   - Mostly EMEA

### Notes

Mention the budget

## Slide 2

Just a text box
"""


def tiny_pdf(text: str) -> bytes:
    """A minimal valid one-page PDF showing text (with a correct xref table)."""
    stream = f"BT /F1 24 Tf 72 720 Td ({text}) Tj ET".encode()
    objs = [b"<< /Type /Catalog /Pages 2 0 R >>",
            b"<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            b"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            b"<< /Length %d >>\nstream\n" % len(stream) + stream + b"\nendstream",
            b"<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"]
    out, offsets = bytearray(b"%PDF-1.4\n"), []
    for i, body in enumerate(objs, 1):
        offsets.append(len(out))
        out += b"%d 0 obj\n" % i + body + b"\nendobj\n"
    xref = len(out)
    out += b"xref\n0 %d\n0000000000 65535 f \n" % (len(objs) + 1)
    out += b"".join(b"%010d 00000 n \n" % o for o in offsets)
    out += b"trailer\n<< /Size %d /Root 1 0 R >>\nstartxref\n%d\n%%%%EOF\n" % (len(objs) + 1, xref)
    return bytes(out)


def run(shell, cmd):
    return shell.run(cmd)


def out(shell, cmd):
    r = shell.run(cmd)
    assert r.exit_code == 0, f"`{cmd}` exited {r.exit_code}: {r.stderr}"
    return r.stdout


class BinaryFileTests(unittest.TestCase):
    def setUp(self):
        self.sh = Shell()
        self.sh.fs.write_bytes("/home/agent/img.png", PNG)

    def test_round_trip_and_sizes(self):
        fs = self.sh.fs
        all_bytes = bytes(range(256))
        fs.write_bytes("/home/agent/all.bin", all_bytes)
        self.assertEqual(fs.read_bytes("/home/agent/all.bin"), all_bytes)
        self.assertEqual(fs.stat("/home/agent/all.bin").size, 256)
        self.assertIn("\ufffd", fs.read_text("/home/agent/all.bin"))  # lossy, no exception
        fs.write_text("/home/agent/t.txt", "héllo")
        self.assertEqual(fs.read_bytes("/home/agent/t.txt"), "héllo".encode())
        self.assertEqual(out(self.sh, "wc -c all.bin t.txt"), "256 all.bin\n  6 t.txt\n262 total\n")
        self.assertEqual(out(self.sh, "stat -c %s all.bin"), "256\n")
        self.assertIn(" 256 ", out(self.sh, "ls -l all.bin"))
        out(self.sh, "cp all.bin copy.bin && mv copy.bin moved.bin")
        self.assertEqual(fs.read_bytes("/home/agent/moved.bin"), all_bytes)
        self.assertEqual(out(self.sh, "head -c 3 t.txt"), "hé")
        self.assertEqual(out(self.sh, "echo more >> all.bin; wc -c all.bin"), "261 all.bin\n")
        self.assertEqual(fs.read_bytes("/home/agent/all.bin")[:256], all_bytes)

    def test_hashes_and_base64_use_real_bytes(self):
        self.assertEqual(out(self.sh, "sha256sum img.png"), f"{hashlib.sha256(PNG).hexdigest()}  img.png\n")
        self.assertEqual(out(self.sh, "md5sum img.png"), f"{hashlib.md5(PNG).hexdigest()}  img.png\n")
        self.assertEqual(out(self.sh, "base64 -w0 img.png"), base64.b64encode(PNG).decode() + "\n")

    def test_cat_and_grep_on_binary(self):
        self.assertEqual(run(self.sh, "cat img.png").exit_code, 0)
        self.assertEqual(out(self.sh, "grep IHDR img.png"), "grep: img.png: binary file matches\n")
        self.assertEqual(out(self.sh, "grep -c IHDR img.png"), "1\n")
        self.assertEqual(run(self.sh, "grep -I IHDR img.png").exit_code, 1)
        self.assertEqual(run(self.sh, "grep nothing img.png").exit_code, 1)

    def test_file_identifies_types(self):
        fs = self.sh.fs
        fs.write_bytes("/home/agent/r.docx", make_docx())
        fs.write_bytes("/home/agent/r.xlsx", make_xlsx())
        fs.write_bytes("/home/agent/r.pptx", make_pptx())
        fs.write_bytes("/home/agent/a.zip", zip_bytes({"a.txt": "x"}))
        fs.write_bytes("/home/agent/d.pdf", tiny_pdf("hi"))
        fs.write_bytes("/home/agent/p.jpg", b"\xff\xd8\xff\xe0\x00\x10JFIF")
        fs.write_bytes("/home/agent/z.gz", b"\x1f\x8b\x08\x00")
        fs.write_bytes("/home/agent/db.sqlite", b"SQLite format 3\x00" + bytes(84))
        fs.write_bytes("/home/agent/x.bin", b"\x00\x01\x02")
        self.assertEqual(out(self.sh, "file img.png r.docx r.xlsx r.pptx a.zip d.pdf p.jpg z.gz db.sqlite x.bin"),
                         "img.png: PNG image data, 2 x 3, 8-bit/color RGBA, non-interlaced\n"
                         "r.docx: Microsoft Word 2007+\nr.xlsx: Microsoft Excel 2007+\nr.pptx: Microsoft PowerPoint 2007+\n"
                         "a.zip: Zip archive data\nd.pdf: PDF document, version 1.4\np.jpg: JPEG image data\n"
                         "z.gz: gzip compressed data\ndb.sqlite: SQLite 3.x database\nx.bin: data\n")

    def test_permission_denied(self):
        self.sh.fs.write_bytes("/home/agent/secret.docx", make_docx())
        self.sh.fs.chmod("/home/agent/secret.docx", 0)
        self.sh.fs.set_owner("/home/agent/secret.docx", "root")
        for cmd in ("to-markdown secret.docx", "xxd secret.docx", "sha256sum secret.docx"):
            r = run(self.sh, cmd)
            self.assertEqual(r.exit_code, 1, cmd)
            self.assertIn("secret.docx: Permission denied", r.stderr)


class ConverterTests(unittest.TestCase):
    def test_docx(self):
        self.assertEqual(to_markdown(make_docx(), "r.docx"), DOCX_MD)

    def test_xlsx(self):
        self.assertEqual(to_markdown(make_xlsx(), "r.xlsx"), XLSX_MD)

    def test_pptx(self):
        self.assertEqual(to_markdown(make_pptx(), "r.pptx"), PPTX_MD)

    def test_format_from_magic_when_extension_unknown(self):
        self.assertEqual(to_markdown(make_docx(), "download.bin"), DOCX_MD)

    def test_html(self):
        html = """<html><head><title>T</title><style>p{color:red}</style><script>var x="<p>no</p>";</script></head>
<body><nav><a href="/">Home</a></nav><h1>Main &amp; Title</h1>
<p>Some <strong>bold</strong>, <em>italic</em> and <a href="https://x.org/a">a link</a>.<br>Second line</p>
<ul><li>One<ul><li>Nested <code>x()</code></li></ul></li><li>Two</li></ul>
<ol><li>First</li><li>Second</li></ol>
<pre><code>def f():
    return 1</code></pre>
<table><tr><th>Name</th><th>Qty</th></tr><tr><td><b>Pen</b></td><td>2</td></tr></table>
<img src="chart.png" alt="Chart"></body></html>"""
        self.assertEqual(to_markdown(html, "page.html"), """# Main & Title

Some **bold**, *italic* and [a link](https://x.org/a).
Second line

- One
   - Nested `x()`
- Two

1. First
2. Second

```
def f():
    return 1
```

| Name | Qty |
| --- | --- |
| **Pen** | 2 |

![Chart](chart.png)
""")

    def test_plain_formats(self):
        self.assertEqual(to_markdown(b'name,note\nA,"x, y"\nB,"multi\nline"\n', "d.csv"),
                         "| name | note |\n| --- | --- |\n| A | x, y |\n| B | multi line |\n")
        self.assertEqual(to_markdown(b"a\tb\n1\t2\n", "d.tsv"), "| a | b |\n| --- | --- |\n| 1 | 2 |\n")
        self.assertEqual(to_markdown(b'{"a":[1,2],"b":"\xc3\xa9"}', "d.json"), '```json\n{\n  "a": [\n    1,\n    2\n  ],\n  "b": "é"\n}\n```\n')
        self.assertEqual(to_markdown(b"# Hi\n\ntext\n", "x.md"), "# Hi\n\ntext\n")
        self.assertEqual(to_markdown(b"plain", "x.txt"), "plain")

    def test_malformed_input_raises_shell_error(self):
        cases = [
            (b"PK\x03\x04 not really a zip", "x.docx"),
            (zip_bytes({"word/document.xml": "<w:document><unclosed"}), "x.docx"),
            (zip_bytes({"other.xml": "<a/>"}), "x.xlsx"),
            (zip_bytes({"ppt/presentation.xml": "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><x>&e;</x>"}), "x.pptx"),
            (b"{not json", "x.json"),
            (PNG, "img.png"),
            (b"\x00\x01", "noext"),
        ]
        for data, name in cases:
            with self.assertRaises(ShellError, msg=name):
                to_markdown(data, name)
        with self.assertRaisesRegex(ShellError, r"supported: \.docx"):
            to_markdown(PNG, "img.png")

    def test_zip_bomb_is_capped(self):
        saved = convert.MAX_UNCOMPRESSED
        convert.MAX_UNCOMPRESSED = 1000
        try:
            bomb = zip_bytes({"word/document.xml": "<w:document " + W + "><w:body>" + " " * 5000 + "</w:body></w:document>"})
            with self.assertRaisesRegex(ShellError, "more than"):
                to_markdown(bomb, "x.docx")
        finally:
            convert.MAX_UNCOMPRESSED = saved

    def test_zip_with_too_many_entries_is_refused_before_it_is_read(self):
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w") as zf:
            zf.writestr("word/document.xml", "<w:document " + W + "/>")
            for i in range(convert.MAX_ZIP_ENTRIES):
                zf.writestr(f"d{i}/", b"")
        many = buf.getvalue()
        lying = bytearray(many)  # the end record says 1 entry; the directory holds them all
        lying[-14:-10] = (1).to_bytes(2, "little") * 2
        for data in (many, bytes(lying)):
            with self.assertRaisesRegex(ShellError, r"^not a valid docx file \(more than 10000 entries in the archive\)$"):
                to_markdown(data, "x.docx")
            sh = Shell()
            sh.fs.write_bytes("/home/agent/x.docx", data)
            self.assertEqual("x.docx: Zip archive data\n", sh.run("file x.docx").stdout)

    def test_pdf_is_not_converted(self):
        # PDF needs a native parser, so it is never run on agent-supplied bytes; the host can attach pdftotext itself
        with self.assertRaisesRegex(ShellError, "pdftotext -layout"):
            to_markdown(tiny_pdf("hi"), "x.pdf")


class ToMarkdownCommandTests(unittest.TestCase):
    def setUp(self):
        self.sh = Shell()
        self.sh.fs.write_bytes("/home/agent/report.docx", make_docx())
        self.sh.fs.write_bytes("/home/agent/data.xlsx", make_xlsx())

    def test_pipes_and_output_file(self):
        self.assertEqual(out(self.sh, "to-markdown report.docx | grep -n Revenue"),
                         "5:Revenue was **record** and *growing*. See [the site](https://example.com/)\n")
        out(self.sh, "to-markdown -o out.md report.docx")
        self.assertEqual(self.sh.fs.read_text("/home/agent/out.md"), DOCX_MD)
        self.assertEqual(out(self.sh, "to-markdown report.docx data.xlsx | grep '^# '"),
                         "# report.docx\n# Annual Report\n# Overview\n# data.xlsx\n")
        self.assertIn("to-markdown", out(self.sh, "help"))

    def test_errors(self):
        r = run(self.sh, "to-markdown missing.docx report.docx")
        self.assertEqual(r.exit_code, 1)
        self.assertEqual(r.stderr, "to-markdown: missing.docx: No such file or directory\n")
        self.assertIn("# Annual Report", r.stdout)
        self.assertEqual(run(self.sh, "to-markdown").exit_code, 1)
        self.sh.fs.write_text("/home/agent/bad.docx", "not a zip")
        r = run(self.sh, "to-markdown bad.docx")
        self.assertEqual(r.exit_code, 1)
        self.assertTrue(r.stderr.startswith("to-markdown: bad.docx: not a valid docx file"), r.stderr)

    def test_add_document(self):
        fs = self.sh.fs
        with tempfile.TemporaryDirectory() as d:
            src = os.path.join(d, "real.docx")
            Path(src).write_bytes(make_docx())
            fs.add_document("/data/docs/real.docx", src, also_markdown=True)
        self.assertEqual(fs.read_bytes("/data/docs/real.docx"), make_docx())
        self.assertEqual(fs.read_text("/data/docs/real.docx.md"), DOCX_MD)
        fs.add_document("/data/raw.xlsx", make_xlsx())
        self.assertFalse(fs.exists("/data/raw.xlsx.md"))
        self.assertEqual(out(self.sh, "to-markdown /data/raw.xlsx | head -1"), "## Sales\n")


SAMPLE = b"Hello, World!\n\x00\x01\x02\xff binary\tstuff here\n"


class BinaryInspectionTests(unittest.TestCase):
    """Expected output captured from xxd 2023-10-25, util-linux hexdump, GNU od and GNU strings."""

    def setUp(self):
        self.sh = Shell()
        self.sh.fs.write_bytes("/home/agent/s.bin", SAMPLE)
        self.sh.fs.write_bytes("/home/agent/z.bin", bytes(100) + b"abcdefghijklmnopqrstuvwxyz\x80\x81")

    CASES = [
        ("xxd s.bin", "00000000: 4865 6c6c 6f2c 2057 6f72 6c64 210a 0001  Hello, World!...\n"
                      "00000010: 02ff 2062 696e 6172 7909 7374 7566 6620  .. binary.stuff \n"
                      "00000020: 6865 7265 0a                             here.\n"),
        ("xxd -l 8 -s 2 s.bin", "00000002: 6c6c 6f2c 2057 6f72                      llo, Wor\n"),
        ("xxd -p s.bin", "48656c6c6f2c20576f726c64210a000102ff2062696e6172790973747566\n6620686572650a\n"),
        ("printf abc | xxd", "00000000: 6162 63                                  abc\n"),
        ("hexdump -C s.bin", "00000000  48 65 6c 6c 6f 2c 20 57  6f 72 6c 64 21 0a 00 01  |Hello, World!...|\n"
                             "00000010  02 ff 20 62 69 6e 61 72  79 09 73 74 75 66 66 20  |.. binary.stuff |\n"
                             "00000020  68 65 72 65 0a                                    |here.|\n00000025\n"),
        ("hexdump -C -n 5 s.bin", "00000000  48 65 6c 6c 6f                                    |Hello|\n00000005\n"),
        ("hexdump -C z.bin", "00000000  00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00  |................|\n*\n"
                             "00000060  00 00 00 00 61 62 63 64  65 66 67 68 69 6a 6b 6c  |....abcdefghijkl|\n"
                             "00000070  6d 6e 6f 70 71 72 73 74  75 76 77 78 79 7a 80 81  |mnopqrstuvwxyz..|\n00000080\n"),
        ("od -c s.bin", "0000000   H   e   l   l   o   ,       W   o   r   l   d   !  \\n  \\0 001\n"
                        "0000020 002 377       b   i   n   a   r   y  \\t   s   t   u   f   f    \n"
                        "0000040   h   e   r   e  \\n\n0000045\n"),
        ("od -x s.bin", "0000000 6548 6c6c 2c6f 5720 726f 646c 0a21 0100\n0000020 ff02 6220 6e69 7261 0979 7473 6675 2066\n"
                        "0000040 6568 6572 000a\n0000045\n"),
        ("od -An -tx1 s.bin", " 48 65 6c 6c 6f 2c 20 57 6f 72 6c 64 21 0a 00 01\n 02 ff 20 62 69 6e 61 72 79 09 73 74 75 66 66 20\n"
                              " 68 65 72 65 0a\n"),
        ("od -N 4 -c s.bin", "0000000   H   e   l   l\n0000004\n"),
        ("strings s.bin", "Hello, World!\n binary\tstuff here\n"),
        ("strings -n 2 z.bin", "abcdefghijklmnopqrstuvwxyz\n"),
        ("echo hi there | strings", "hi there\n"),
    ]

    def test_matches_real_tools(self):
        for cmd, expected in self.CASES:
            with self.subTest(cmd=cmd):
                self.assertEqual(out(self.sh, cmd), expected)

    def test_errors_and_help(self):
        r = run(self.sh, "xxd nope.bin")
        self.assertEqual((r.exit_code, r.stderr), (1, "xxd: nope.bin: No such file or directory\n"))
        help_text = out(self.sh, "help")
        for name in ("xxd", "hexdump", "od", "strings"):
            self.assertIn(name, help_text)


@unittest.skipUnless(shutil.which("soffice"), "LibreOffice not installed")
class RealWorldDocumentTests(unittest.TestCase):
    """Documents written by LibreOffice rather than by hand."""

    def test_libreoffice_documents(self):
        with tempfile.TemporaryDirectory() as d:
            Path(d, "doc.html").write_text(
                "<html><body><h1>Quarterly Report</h1><p>Revenue grew <b>12%</b>. See <a href='https://example.com/q3'>details</a>.</p>"
                "<ul><li>First point</li><li>Second point</li></ul>"
                "<table border=1><tr><th>Region</th><th>Sales</th></tr><tr><td>North</td><td>100</td></tr></table></body></html>")
            Path(d, "sheet.csv").write_text("Name,Qty\nApple,3\nPear,10\n")
            Path(d, "deck.fodp").write_text(FODP)
            for src, target in (("doc.html", "docx:MS Word 2007 XML"), ("sheet.csv", "xlsx"), ("deck.fodp", "pptx")):
                subprocess.run(["soffice", "--headless", "--convert-to", target, "--outdir", d, os.path.join(d, src)],
                               capture_output=True, timeout=180)
            docx = Path(d, "doc.docx").read_bytes()
            xlsx = Path(d, "sheet.xlsx").read_bytes()
            pptx = Path(d, "deck.pptx").read_bytes()
        md = to_markdown(docx, "doc.docx")
        for part in ("# Quarterly Report", "Revenue grew **12%**", "[details](https://example.com/q3)",
                     "- First point\n- Second point", "| Region | Sales |\n| --- | --- |\n| North | 100 |"):
            self.assertIn(part, md)
        self.assertEqual(to_markdown(xlsx, "sheet.xlsx"), "## sheet\n\n| Name | Qty |\n| --- | --- |\n| Apple | 3 |\n| Pear | 10 |\n")
        # LibreOffice exports these frames as text boxes, not placeholders, so there is no title to promote.
        self.assertEqual(to_markdown(pptx, "deck.pptx"), "## Slide 1\n\nLaunch Plan\n\n- Ship beta in May\n- Hire two engineers\n\n"
                                                         "### Notes\n\nRemember the budget\n\n## Slide 2\n\nRisks\n")


FODP = """<?xml version="1.0" encoding="UTF-8"?>
<office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" \
xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" \
xmlns:presentation="urn:oasis:names:tc:opendocument:xmlns:presentation:1.0" \
xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0" office:version="1.2" \
office:mimetype="application/vnd.oasis.opendocument.presentation"><office:body><office:presentation>
<draw:page draw:name="p1">
<draw:frame presentation:class="title" svg:x="1cm" svg:y="1cm" svg:width="20cm" svg:height="3cm"><draw:text-box>\
<text:p>Launch Plan</text:p></draw:text-box></draw:frame>
<draw:frame presentation:class="outline" svg:x="1cm" svg:y="5cm" svg:width="20cm" svg:height="10cm"><draw:text-box><text:list>\
<text:list-item><text:p>Ship beta in May</text:p></text:list-item><text:list-item><text:p>Hire two engineers</text:p></text:list-item>\
</text:list></draw:text-box></draw:frame>
<presentation:notes><draw:frame presentation:class="notes" svg:x="1cm" svg:y="5cm" svg:width="20cm" svg:height="10cm">\
<draw:text-box><text:p>Remember the budget</text:p></draw:text-box></draw:frame></presentation:notes>
</draw:page>
<draw:page draw:name="p2">
<draw:frame presentation:class="title" svg:x="1cm" svg:y="1cm" svg:width="20cm" svg:height="3cm"><draw:text-box>\
<text:p>Risks</text:p></draw:text-box></draw:frame>
</draw:page>
</office:presentation></office:body></office:document>
"""


if __name__ == "__main__":
    unittest.main()
