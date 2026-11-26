using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Topwater.Tests;

/// <summary>Binary files in the virtual filesystem, document-to-Markdown conversion and binary inspection commands.
/// Mirrors python/tests/test_documents.py. One class, so tests that swap MarkdownConverter's static hooks never run in parallel.</summary>
public class DocumentTests
{
    const string W = "xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
    const string S = "xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";
    const string P = "xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:p=\"http://schemas.openxmlformats.org/presentationml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"";

    static readonly byte[] Png = [.. Convert.FromHexString("89504e470d0a1a0a0000000d4948445200000002000000030806000000"), 0, 1, 2, 0xff];

    static async Task<ShellResult> Run(Shell shell, string cmd) => await shell.RunAsync(cmd);

    static async Task<string> Out(Shell shell, string cmd)
    {
        var r = await shell.RunAsync(cmd);
        Assert.True(r.ExitCode == 0, $"`{cmd}` exited {r.ExitCode}: {r.Stderr}");
        return r.Stdout;
    }

    static byte[] Zip(params (string Name, string Text)[] parts)
    {
        var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, text) in parts)
            {
                using var w = new StreamWriter(z.CreateEntry(name).Open());
                w.Write(text);
            }
        return ms.ToArray();
    }

    static string Rels(params (string Id, string Target)[] items) =>
        "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
        string.Concat(items.Select(i => $"<Relationship Id=\"{i.Id}\" Type=\"t\" Target=\"{i.Target}\"{(i.Target.StartsWith("http") ? " TargetMode=\"External\"" : "")}/>")) +
        "</Relationships>";

    static byte[] MakeDocx()
    {
        static string Para(string text, string? style = null, (string Id, string Level)? num = null) =>
            "<w:p><w:pPr>" + (style != null ? $"<w:pStyle w:val=\"{style}\"/>" : "") +
            (num is { } n ? $"<w:numPr><w:ilvl w:val=\"{n.Level}\"/><w:numId w:val=\"{n.Id}\"/></w:numPr>" : "") +
            $"</w:pPr><w:r><w:t xml:space=\"preserve\">{text}</w:t></w:r></w:p>";
        var body = Para("Annual Report", "Title") + Para("Overview", "Heading1") +
            "<w:p><w:r><w:t xml:space=\"preserve\">Revenue was </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>record</w:t></w:r>" +
            "<w:r><w:t xml:space=\"preserve\"> and </w:t></w:r><w:r><w:rPr><w:i/></w:rPr><w:t>growing</w:t></w:r>" +
            "<w:r><w:t xml:space=\"preserve\">. See </w:t></w:r><w:hyperlink r:id=\"rId9\"><w:r><w:t>the site</w:t></w:r></w:hyperlink></w:p>" +
            Para("Details", "Heading2") +
            Para("Apples", num: ("1", "0")) + Para("Green", num: ("1", "1")) + Para("Pears", num: ("1", "0")) +
            Para("First step", num: ("2", "0")) + Para("Second step", num: ("2", "0")) +
            "<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Region</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Sales</w:t></w:r></w:p></w:tc></w:tr>" +
            "<w:tr><w:tc><w:p><w:r><w:t>North|East</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>100</w:t></w:r></w:p></w:tc></w:tr></w:tbl>";
        var numbering = $"<w:numbering {W}>" +
            "<w:abstractNum w:abstractNumId=\"10\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"bullet\"/></w:lvl><w:lvl w:ilvl=\"1\"><w:numFmt w:val=\"bullet\"/></w:lvl></w:abstractNum>" +
            "<w:abstractNum w:abstractNumId=\"20\"><w:lvl w:ilvl=\"0\"><w:numFmt w:val=\"decimal\"/></w:lvl></w:abstractNum>" +
            "<w:num w:numId=\"1\"><w:abstractNumId w:val=\"10\"/></w:num><w:num w:numId=\"2\"><w:abstractNumId w:val=\"20\"/></w:num></w:numbering>";
        return Zip(("[Content_Types].xml", "<Types/>"), ("word/document.xml", $"<w:document {W}><w:body>{body}</w:body></w:document>"),
            ("word/numbering.xml", numbering), ("word/_rels/document.xml.rels", Rels(("rId9", "https://example.com/"))));
    }

    const string DocxMd = """
        # Annual Report

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
        | North\|East | 100 |

        """;

    static byte[] MakeXlsx()
    {
        var sheet1 = $"<worksheet {S}><sheetData>" +
            "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"inlineStr\"><is><t>Ok</t></is></c></row>" +
            "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>2</v></c><c r=\"B2\"><v>3.5</v></c><c r=\"C2\" t=\"b\"><v>1</v></c></row>" +
            "<row r=\"4\"><c r=\"A4\" t=\"str\"><f>\"x\"&amp;\"y\"</f><v>xy</v></c><c r=\"B4\"><f>B2*2</f><v>7</v></c><c r=\"C4\" t=\"b\"><v>0</v></c></row>" +
            "</sheetData></worksheet>";
        var sheet2 = $"<worksheet {S}><sheetData><row r=\"1\"><c r=\"B1\"><v>42</v></c></row></sheetData></worksheet>";
        return Zip(
            ("xl/workbook.xml", $"<workbook {S}><sheets><sheet name=\"Sales\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Other\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", Rels(("rId1", "worksheets/sheet1.xml"), ("rId2", "/xl/worksheets/sheet2.xml"))),
            ("xl/sharedStrings.xml", $"<sst {S}><si><t>Item</t></si><si><t>Price</t></si><si><r><t>Wid</t></r><r><t>get</t></r></si></sst>"),
            ("xl/worksheets/sheet1.xml", sheet1), ("xl/worksheets/sheet2.xml", sheet2));
    }

    const string XlsxMd = """
        ## Sales

        | Item | Price | Ok |
        | --- | --- | --- |
        | Widget | 3.5 | TRUE |
        | xy | 7 | FALSE |

        ## Other

        | 42 |
        | --- |

        """;

    static byte[] MakePptx()
    {
        static string Sp(string? ph, params (string Text, int Level)[] paras) =>
            "<p:sp><p:nvSpPr><p:cNvPr id=\"1\" name=\"\"/><p:cNvSpPr/>" + (ph != null ? $"<p:nvPr><p:ph type=\"{ph}\"/></p:nvPr>" : "<p:nvPr/>") +
            "</p:nvSpPr><p:txBody>" + string.Concat(paras.Select(p => "<a:p>" + (p.Level > 0 ? $"<a:pPr lvl=\"{p.Level}\"/>" : "") + $"<a:r><a:t>{p.Text}</a:t></a:r></a:p>")) +
            "</p:txBody></p:sp>";
        static string Slide(params string[] shapes) => $"<p:sld {P}><p:cSld><p:spTree>{string.Concat(shapes)}</p:spTree></p:cSld></p:sld>";
        return Zip(
            ("ppt/presentation.xml", $"<p:presentation {P}><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId2\"/><p:sldId id=\"257\" r:id=\"rId3\"/></p:sldIdLst></p:presentation>"),
            ("ppt/_rels/presentation.xml.rels", Rels(("rId2", "slides/slide1.xml"), ("rId3", "slides/slide2.xml"))),
            ("ppt/slides/slide1.xml", Slide(Sp("title", ("Q3 Results", 0)), Sp("body", ("Revenue up", 0), ("Mostly EMEA", 1)), Sp("sldNum", ("7", 0)))),
            ("ppt/slides/_rels/slide1.xml.rels", Rels(("rId1", "../notesSlides/notesSlide1.xml"))),
            ("ppt/notesSlides/notesSlide1.xml", Slide(Sp("sldImg"), Sp("body", ("Mention the budget", 0)))),
            ("ppt/slides/slide2.xml", Slide(Sp(null, ("Just a text box", 0)))));
    }

    const string PptxMd = """
        ## Q3 Results

        - Revenue up
           - Mostly EMEA

        ### Notes

        Mention the budget

        ## Slide 2

        Just a text box

        """;

    /// <summary>A minimal valid one-page PDF showing text (with a correct xref table).</summary>
    static byte[] TinyPdf(string text)
    {
        var stream = $"BT /F1 24 Tf 72 720 Td ({text}) Tj ET";
        string[] objs =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        ];
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objs.Length; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        int xref = sb.Length;
        sb.Append($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }

    static string Md(byte[] data, string name) => MarkdownConverter.ToMarkdown(data, name);

    // ---------- binary files ----------

    [Fact]
    public async Task RoundTripAndSizes()
    {
        var sh = new Shell();
        var fs = sh.Fs;
        var all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        fs.WriteAllBytes("/home/agent/all.bin", all);
        Assert.Equal(all, fs.ReadAllBytes("/home/agent/all.bin"));
        Assert.Equal(256, fs.Stat("/home/agent/all.bin").Size);
        Assert.Contains("\uFFFD", fs.ReadAllText("/home/agent/all.bin")); // lossy, no exception
        fs.WriteAllText("/home/agent/t.txt", "héllo");
        Assert.Equal(Encoding.UTF8.GetBytes("héllo"), fs.ReadAllBytes("/home/agent/t.txt"));
        Assert.Equal("256 all.bin\n  6 t.txt\n262 total\n", await Out(sh, "wc -c all.bin t.txt"));
        Assert.Equal("256\n", await Out(sh, "stat -c %s all.bin"));
        Assert.Contains(" 256 ", await Out(sh, "ls -l all.bin"));
        await Out(sh, "cp all.bin copy.bin && mv copy.bin moved.bin");
        Assert.Equal(all, fs.ReadAllBytes("/home/agent/moved.bin"));
        Assert.Equal("hé", await Out(sh, "head -c 3 t.txt"));
        Assert.Equal("261 all.bin\n", await Out(sh, "echo more >> all.bin; wc -c all.bin"));
        Assert.Equal(all, fs.ReadAllBytes("/home/agent/all.bin")[..256]);
    }

    [Fact]
    public async Task HashesAndBase64UseRealBytes()
    {
        var sh = new Shell();
        sh.Fs.WriteAllBytes("/home/agent/img.png", Png);
        Assert.Equal($"{Convert.ToHexString(SHA256.HashData(Png)).ToLowerInvariant()}  img.png\n", await Out(sh, "sha256sum img.png"));
        Assert.Equal($"{Convert.ToHexString(MD5.HashData(Png)).ToLowerInvariant()}  img.png\n", await Out(sh, "md5sum img.png"));
        Assert.Equal(Convert.ToBase64String(Png) + "\n", await Out(sh, "base64 -w0 img.png"));
    }

    [Fact]
    public async Task CatAndGrepOnBinary()
    {
        var sh = new Shell();
        sh.Fs.WriteAllBytes("/home/agent/img.png", Png);
        Assert.Equal(0, (await Run(sh, "cat img.png")).ExitCode);
        Assert.Equal("grep: img.png: binary file matches\n", await Out(sh, "grep IHDR img.png"));
        Assert.Equal("1\n", await Out(sh, "grep -c IHDR img.png"));
        Assert.Equal(1, (await Run(sh, "grep -I IHDR img.png")).ExitCode);
        Assert.Equal(1, (await Run(sh, "grep nothing img.png")).ExitCode);
    }

    [Fact]
    public async Task FileIdentifiesTypes()
    {
        var sh = new Shell();
        var fs = sh.Fs;
        fs.WriteAllBytes("/home/agent/img.png", Png);
        fs.WriteAllBytes("/home/agent/r.docx", MakeDocx());
        fs.WriteAllBytes("/home/agent/r.xlsx", MakeXlsx());
        fs.WriteAllBytes("/home/agent/r.pptx", MakePptx());
        fs.WriteAllBytes("/home/agent/a.zip", Zip(("a.txt", "x")));
        fs.WriteAllBytes("/home/agent/d.pdf", TinyPdf("hi"));
        fs.WriteAllBytes("/home/agent/p.jpg", [0xff, 0xd8, 0xff, 0xe0, 0, 0x10, (byte)'J', (byte)'F']);
        fs.WriteAllBytes("/home/agent/z.gz", [0x1f, 0x8b, 8, 0]);
        fs.WriteAllBytes("/home/agent/db.sqlite", [.. Encoding.ASCII.GetBytes("SQLite format 3\0"), .. new byte[84]]);
        fs.WriteAllBytes("/home/agent/x.bin", [0, 1, 2]);
        Assert.Equal(
            "img.png: PNG image data, 2 x 3, 8-bit/color RGBA, non-interlaced\n" +
            "r.docx: Microsoft Word 2007+\nr.xlsx: Microsoft Excel 2007+\nr.pptx: Microsoft PowerPoint 2007+\n" +
            "a.zip: Zip archive data\nd.pdf: PDF document, version 1.4\np.jpg: JPEG image data\n" +
            "z.gz: gzip compressed data\ndb.sqlite: SQLite 3.x database\nx.bin: data\n",
            await Out(sh, "file img.png r.docx r.xlsx r.pptx a.zip d.pdf p.jpg z.gz db.sqlite x.bin"));
    }

    [Fact]
    public async Task PermissionDenied()
    {
        var sh = new Shell();
        sh.Fs.WriteAllBytes("/home/agent/secret.docx", MakeDocx());
        sh.Fs.Chmod("/home/agent/secret.docx", 0);
        sh.Fs.SetOwner("/home/agent/secret.docx", "root");
        foreach (var cmd in new[] { "to-markdown secret.docx", "xxd secret.docx", "sha256sum secret.docx" })
        {
            var r = await Run(sh, cmd);
            Assert.Equal(1, r.ExitCode);
            Assert.Contains("secret.docx: Permission denied", r.Stderr);
        }
    }

    // ---------- converters ----------

    [Fact] public void Docx() => Assert.Equal(DocxMd, Md(MakeDocx(), "r.docx"));
    [Fact] public void Xlsx() => Assert.Equal(XlsxMd, Md(MakeXlsx(), "r.xlsx"));
    [Fact] public void Pptx() => Assert.Equal(PptxMd, Md(MakePptx(), "r.pptx"));
    [Fact] public void FormatFromMagicWhenExtensionUnknown() => Assert.Equal(DocxMd, Md(MakeDocx(), "download.bin"));

    [Fact]
    public void Html()
    {
        var html = """
            <html><head><title>T</title><style>p{color:red}</style><script>var x="<p>no</p>";</script></head>
            <body><nav><a href="/">Home</a></nav><h1>Main &amp; Title</h1>
            <p>Some <strong>bold</strong>, <em>italic</em> and <a href="https://x.org/a">a link</a>.<br>Second line</p>
            <ul><li>One<ul><li>Nested <code>x()</code></li></ul></li><li>Two</li></ul>
            <ol><li>First</li><li>Second</li></ol>
            <pre><code>def f():
                return 1</code></pre>
            <table><tr><th>Name</th><th>Qty</th></tr><tr><td><b>Pen</b></td><td>2</td></tr></table>
            <img src="chart.png" alt="Chart"></body></html>
            """;
        Assert.Equal("""
            # Main & Title

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

            """, Md(Encoding.UTF8.GetBytes(html), "page.html"));
    }

    [Fact]
    public void PlainFormats()
    {
        Assert.Equal("| name | note |\n| --- | --- |\n| A | x, y |\n| B | multi line |\n", Md("name,note\nA,\"x, y\"\nB,\"multi\nline\"\n"u8.ToArray(), "d.csv"));
        Assert.Equal("| a | b |\n| --- | --- |\n| 1 | 2 |\n", Md("a\tb\n1\t2\n"u8.ToArray(), "d.tsv"));
        Assert.Equal("```json\n{\n  \"a\": [\n    1,\n    2\n  ],\n  \"b\": \"é\"\n}\n```\n", Md("{\"a\":[1,2],\"b\":\"é\"}"u8.ToArray(), "d.json"));
        Assert.Equal("# Hi\n\ntext\n", Md("# Hi\n\ntext\n"u8.ToArray(), "x.md"));
        Assert.Equal("plain", Md("plain"u8.ToArray(), "x.txt"));
    }

    [Fact]
    public void MalformedInputThrowsShellException()
    {
        var cases = new (byte[] Data, string Name)[]
        {
            ("PK\x03\x04 not really a zip"u8.ToArray(), "x.docx"),
            (Zip(("word/document.xml", "<w:document><unclosed")), "x.docx"),
            (Zip(("other.xml", "<a/>")), "x.xlsx"),
            (Zip(("ppt/presentation.xml", "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><x>&e;</x>")), "x.pptx"),
            ("{not json"u8.ToArray(), "x.json"),
            (Png, "img.png"),
            ([0, 1], "noext"),
        };
        foreach (var (data, name) in cases) Assert.Throws<ShellException>(() => Md(data, name));
        Assert.Contains("supported: .docx", Assert.Throws<ShellException>(() => Md(Png, "img.png")).Message);
    }

    [Fact]
    public void ZipBombIsCapped()
    {
        var saved = MarkdownConverter.MaxUncompressedBytes;
        MarkdownConverter.MaxUncompressedBytes = 1000;
        try
        {
            var bomb = Zip(("word/document.xml", $"<w:document {W}><w:body>{new string(' ', 5000)}</w:body></w:document>"));
            Assert.Contains("more than", Assert.Throws<ShellException>(() => Md(bomb, "x.docx")).Message);
        }
        finally { MarkdownConverter.MaxUncompressedBytes = saved; }
    }

    [Fact]
    public async Task ZipWithTooManyEntriesIsRefusedBeforeItIsRead()
    {
        var many = Zip([("word/document.xml", $"<w:document {W}/>"), .. Enumerable.Range(0, 10_000).Select(i => ($"d{i}/", ""))]);
        var lying = (byte[])many.Clone(); // the end record says 1 entry; the directory holds them all
        lying[^14] = 1; lying[^13] = 0; lying[^12] = 1; lying[^11] = 0;
        foreach (var data in new[] { many, lying })
        {
            Assert.Equal("not a valid docx file (more than 10000 entries in the archive)", Assert.Throws<ShellException>(() => Md(data, "x.docx")).Message);
            var sh = new Shell();
            sh.Fs.WriteAllBytes("/home/agent/x.docx", data);
            Assert.Equal("x.docx: Zip archive data\n", await Out(sh, "file x.docx"));
        }
    }

    [Fact] // PDF needs a native parser, so it is never run on agent-supplied bytes; the host can attach pdftotext itself
    public void PdfIsNotConverted() =>
        Assert.Contains("pdftotext -layout", Assert.Throws<ShellException>(() => Md(TinyPdf("hi"), "x.pdf")).Message);

    // ---------- to-markdown ----------

    static Shell DocShell()
    {
        var sh = new Shell();
        sh.Fs.WriteAllBytes("/home/agent/report.docx", MakeDocx());
        sh.Fs.WriteAllBytes("/home/agent/data.xlsx", MakeXlsx());
        return sh;
    }

    [Fact]
    public async Task ToMarkdownPipesAndOutputFile()
    {
        var sh = DocShell();
        Assert.Equal("5:Revenue was **record** and *growing*. See [the site](https://example.com/)\n", await Out(sh, "to-markdown report.docx | grep -n Revenue"));
        await Out(sh, "to-markdown -o out.md report.docx");
        Assert.Equal(DocxMd, sh.Fs.ReadAllText("/home/agent/out.md"));
        Assert.Equal("# report.docx\n# Annual Report\n# Overview\n# data.xlsx\n", await Out(sh, "to-markdown report.docx data.xlsx | grep '^# '"));
        Assert.Contains("to-markdown", await Out(sh, "help"));
    }

    [Fact]
    public async Task ToMarkdownErrors()
    {
        var sh = DocShell();
        var r = await Run(sh, "to-markdown missing.docx report.docx");
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("to-markdown: missing.docx: No such file or directory\n", r.Stderr);
        Assert.Contains("# Annual Report", r.Stdout);
        Assert.Equal(1, (await Run(sh, "to-markdown")).ExitCode);
        sh.Fs.WriteAllText("/home/agent/bad.docx", "not a zip");
        r = await Run(sh, "to-markdown bad.docx");
        Assert.Equal(1, r.ExitCode);
        Assert.StartsWith("to-markdown: bad.docx: not a valid docx file", r.Stderr);
    }

    [Fact]
    public async Task AddDocument()
    {
        var sh = DocShell();
        var fs = sh.Fs;
        var dir = Directory.CreateTempSubdirectory();
        var docx = MakeDocx();   // once: each build stamps the current time into the zip entries
        try
        {
            var src = Path.Combine(dir.FullName, "real.docx");
            File.WriteAllBytes(src, docx);
            fs.AddDocument("/data/docs/real.docx", src, alsoMarkdown: true);
        }
        finally { dir.Delete(true); }
        Assert.Equal(docx, fs.ReadAllBytes("/data/docs/real.docx"));
        Assert.Equal(DocxMd, fs.ReadAllText("/data/docs/real.docx.md"));
        fs.AddDocument("/data/raw.xlsx", MakeXlsx());
        Assert.False(fs.Exists("/data/raw.xlsx.md"));
        Assert.Equal("## Sales\n", await Out(sh, "to-markdown /data/raw.xlsx | head -1"));
    }

    // ---------- binary inspection (expected output captured from xxd, util-linux hexdump, GNU od and GNU strings) ----------

    static readonly byte[] Sample = "Hello, World!\n\0\x01\x02"u8.ToArray().Append((byte)0xff).Concat(" binary\tstuff here\n"u8.ToArray()).ToArray();

    [Theory]
    [InlineData("xxd s.bin", "00000000: 4865 6c6c 6f2c 2057 6f72 6c64 210a 0001  Hello, World!...\n" +
                             "00000010: 02ff 2062 696e 6172 7909 7374 7566 6620  .. binary.stuff \n" +
                             "00000020: 6865 7265 0a                             here.\n")]
    [InlineData("xxd -l 8 -s 2 s.bin", "00000002: 6c6c 6f2c 2057 6f72                      llo, Wor\n")]
    [InlineData("xxd -p s.bin", "48656c6c6f2c20576f726c64210a000102ff2062696e6172790973747566\n6620686572650a\n")]
    [InlineData("printf abc | xxd", "00000000: 6162 63                                  abc\n")]
    [InlineData("hexdump -C s.bin", "00000000  48 65 6c 6c 6f 2c 20 57  6f 72 6c 64 21 0a 00 01  |Hello, World!...|\n" +
                                    "00000010  02 ff 20 62 69 6e 61 72  79 09 73 74 75 66 66 20  |.. binary.stuff |\n" +
                                    "00000020  68 65 72 65 0a                                    |here.|\n00000025\n")]
    [InlineData("hexdump -C -n 5 s.bin", "00000000  48 65 6c 6c 6f                                    |Hello|\n00000005\n")]
    [InlineData("hexdump -C z.bin", "00000000  00 00 00 00 00 00 00 00  00 00 00 00 00 00 00 00  |................|\n*\n" +
                                    "00000060  00 00 00 00 61 62 63 64  65 66 67 68 69 6a 6b 6c  |....abcdefghijkl|\n" +
                                    "00000070  6d 6e 6f 70 71 72 73 74  75 76 77 78 79 7a 80 81  |mnopqrstuvwxyz..|\n00000080\n")]
    [InlineData("od -c s.bin", "0000000   H   e   l   l   o   ,       W   o   r   l   d   !  \\n  \\0 001\n" +
                               "0000020 002 377       b   i   n   a   r   y  \\t   s   t   u   f   f    \n" +
                               "0000040   h   e   r   e  \\n\n0000045\n")]
    [InlineData("od -x s.bin", "0000000 6548 6c6c 2c6f 5720 726f 646c 0a21 0100\n0000020 ff02 6220 6e69 7261 0979 7473 6675 2066\n" +
                               "0000040 6568 6572 000a\n0000045\n")]
    [InlineData("od -An -tx1 s.bin", " 48 65 6c 6c 6f 2c 20 57 6f 72 6c 64 21 0a 00 01\n 02 ff 20 62 69 6e 61 72 79 09 73 74 75 66 66 20\n" +
                                     " 68 65 72 65 0a\n")]
    [InlineData("od -N 4 -c s.bin", "0000000   H   e   l   l\n0000004\n")]
    [InlineData("strings s.bin", "Hello, World!\n binary\tstuff here\n")]
    [InlineData("strings -n 2 z.bin", "abcdefghijklmnopqrstuvwxyz\n")]
    [InlineData("echo hi there | strings", "hi there\n")]
    public async Task InspectionMatchesRealTools(string cmd, string expected)
    {
        var sh = new Shell();
        sh.Fs.WriteAllBytes("/home/agent/s.bin", Sample);
        sh.Fs.WriteAllBytes("/home/agent/z.bin", [.. new byte[100], .. "abcdefghijklmnopqrstuvwxyz"u8.ToArray(), 0x80, 0x81]);
        Assert.Equal(expected, await Out(sh, cmd));
    }

    [Fact]
    public async Task InspectionErrorsAndHelp()
    {
        var sh = new Shell();
        var r = await Run(sh, "xxd nope.bin");
        Assert.Equal((1, "xxd: nope.bin: No such file or directory\n"), (r.ExitCode, r.Stderr));
        var help = await Out(sh, "help");
        foreach (var name in new[] { "xxd", "hexdump", "od", "strings" }) Assert.Contains(name, help);
    }

    // ---------- real-world documents ----------

    const string Fodp = """
        <?xml version="1.0" encoding="UTF-8"?>
        <office:document xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:presentation="urn:oasis:names:tc:opendocument:xmlns:presentation:1.0" xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0" office:version="1.2" office:mimetype="application/vnd.oasis.opendocument.presentation"><office:body><office:presentation>
        <draw:page draw:name="p1">
        <draw:frame presentation:class="title" svg:x="1cm" svg:y="1cm" svg:width="20cm" svg:height="3cm"><draw:text-box><text:p>Launch Plan</text:p></draw:text-box></draw:frame>
        <draw:frame presentation:class="outline" svg:x="1cm" svg:y="5cm" svg:width="20cm" svg:height="10cm"><draw:text-box><text:list><text:list-item><text:p>Ship beta in May</text:p></text:list-item><text:list-item><text:p>Hire two engineers</text:p></text:list-item></text:list></draw:text-box></draw:frame>
        <presentation:notes><draw:frame presentation:class="notes" svg:x="1cm" svg:y="5cm" svg:width="20cm" svg:height="10cm"><draw:text-box><text:p>Remember the budget</text:p></draw:text-box></draw:frame></presentation:notes>
        </draw:page>
        <draw:page draw:name="p2">
        <draw:frame presentation:class="title" svg:x="1cm" svg:y="1cm" svg:width="20cm" svg:height="3cm"><draw:text-box><text:p>Risks</text:p></draw:text-box></draw:frame>
        </draw:page>
        </office:presentation></office:body></office:document>
        """;

    /// <summary>Documents written by LibreOffice rather than by hand (skipped when soffice isn't installed).</summary>
    [Fact]
    public void LibreOfficeDocuments()
    {
        var soffice = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(d => Path.Combine(d, "soffice")).FirstOrDefault(File.Exists);
        if (soffice == null) return;
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "doc.html"),
                "<html><body><h1>Quarterly Report</h1><p>Revenue grew <b>12%</b>. See <a href='https://example.com/q3'>details</a>.</p>" +
                "<ul><li>First point</li><li>Second point</li></ul>" +
                "<table border=1><tr><th>Region</th><th>Sales</th></tr><tr><td>North</td><td>100</td></tr></table></body></html>");
            File.WriteAllText(Path.Combine(dir.FullName, "sheet.csv"), "Name,Qty\nApple,3\nPear,10\n");
            File.WriteAllText(Path.Combine(dir.FullName, "deck.fodp"), Fodp);
            foreach (var (src, target) in new[] { ("doc.html", "docx:MS Word 2007 XML"), ("sheet.csv", "xlsx"), ("deck.fodp", "pptx") })
            {
                var psi = new ProcessStartInfo(soffice) { RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var arg in new[] { "--headless", "--convert-to", target, "--outdir", dir.FullName, Path.Combine(dir.FullName, src) }) psi.ArgumentList.Add(arg);
                using var p = Process.Start(psi)!;
                p.WaitForExit(180_000);
            }
            var md = Md(File.ReadAllBytes(Path.Combine(dir.FullName, "doc.docx")), "doc.docx");
            foreach (var part in new[] { "# Quarterly Report", "Revenue grew **12%**", "[details](https://example.com/q3)",
                         "- First point\n- Second point", "| Region | Sales |\n| --- | --- |\n| North | 100 |" })
                Assert.Contains(part, md);
            Assert.Equal("## sheet\n\n| Name | Qty |\n| --- | --- |\n| Apple | 3 |\n| Pear | 10 |\n", Md(File.ReadAllBytes(Path.Combine(dir.FullName, "sheet.xlsx")), "sheet.xlsx"));
            // LibreOffice exports these frames as text boxes, not placeholders, so there is no title to promote.
            Assert.Equal("## Slide 1\n\nLaunch Plan\n\n- Ship beta in May\n- Hire two engineers\n\n### Notes\n\nRemember the budget\n\n## Slide 2\n\nRisks\n",
                Md(File.ReadAllBytes(Path.Combine(dir.FullName, "deck.pptx")), "deck.pptx"));
        }
        finally { dir.Delete(true); }
    }
}
