import { describe, expect, it } from 'vitest';
import { Shell } from '../src/index.js';
import { addDocument, converterLimits, registerDocuments, toMarkdown } from '../src/commands/documents.js';
import { buildZip } from '../src/commands/zipfile.js';

const W = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"';
const S = 'xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"';
const P = 'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"';

const enc = (s: string) => new TextEncoder().encode(s);
const rels = (...items: [string, string][]) =>
  '<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">' +
  items.map(([id, target]) => `<Relationship Id="${id}" Type="t" Target="${target}"${target.startsWith('http') ? ' TargetMode="External"' : ''}/>`).join('') + '</Relationships>';

function makeDocx() {
  const para = (text: string, style?: string, num?: [string, string]) =>
    '<w:p><w:pPr>' + (style ? `<w:pStyle w:val="${style}"/>` : '') + (num ? `<w:numPr><w:ilvl w:val="${num[1]}"/><w:numId w:val="${num[0]}"/></w:numPr>` : '') +
    `</w:pPr><w:r><w:t xml:space="preserve">${text}</w:t></w:r></w:p>`;
  const body = para('Annual Report', 'Title') + para('Overview', 'Heading1') +
    '<w:p><w:r><w:t xml:space="preserve">Revenue was </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>record</w:t></w:r>' +
    '<w:r><w:t xml:space="preserve"> and </w:t></w:r><w:r><w:rPr><w:i/></w:rPr><w:t>growing</w:t></w:r>' +
    '<w:r><w:t xml:space="preserve">. See </w:t></w:r><w:hyperlink r:id="rId9"><w:r><w:t>the site</w:t></w:r></w:hyperlink></w:p>' +
    para('Details', 'Heading2') +
    para('Apples', undefined, ['1', '0']) + para('Green', undefined, ['1', '1']) + para('Pears', undefined, ['1', '0']) +
    para('First step', undefined, ['2', '0']) + para('Second step', undefined, ['2', '0']) +
    '<w:tbl><w:tr><w:tc><w:p><w:r><w:t>Region</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>Sales</w:t></w:r></w:p></w:tc></w:tr>' +
    '<w:tr><w:tc><w:p><w:r><w:t>North|East</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>100</w:t></w:r></w:p></w:tc></w:tr></w:tbl>';
  const numbering = `<w:numbering ${W}>` +
    '<w:abstractNum w:abstractNumId="10"><w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/></w:lvl><w:lvl w:ilvl="1"><w:numFmt w:val="bullet"/></w:lvl></w:abstractNum>' +
    '<w:abstractNum w:abstractNumId="20"><w:lvl w:ilvl="0"><w:numFmt w:val="decimal"/></w:lvl></w:abstractNum>' +
    '<w:num w:numId="1"><w:abstractNumId w:val="10"/></w:num><w:num w:numId="2"><w:abstractNumId w:val="20"/></w:num></w:numbering>';
  return buildZip([['[Content_Types].xml', '<Types/>'], ['word/document.xml', `<w:document ${W}><w:body>${body}</w:body></w:document>`],
    ['word/numbering.xml', numbering], ['word/_rels/document.xml.rels', rels(['rId9', 'https://example.com/'])]]);
}

const DOCX_MD = `# Annual Report

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
`;

function makeXlsx() {
  const sheet1 = `<worksheet ${S}><sheetData>` +
    '<row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="inlineStr"><is><t>Ok</t></is></c></row>' +
    '<row r="2"><c r="A2" t="s"><v>2</v></c><c r="B2"><v>3.5</v></c><c r="C2" t="b"><v>1</v></c></row>' +
    '<row r="4"><c r="A4" t="str"><f>"x"&amp;"y"</f><v>xy</v></c><c r="B4"><f>B2*2</f><v>7</v></c><c r="C4" t="b"><v>0</v></c></row>' +
    '</sheetData></worksheet>';
  const sheet2 = `<worksheet ${S}><sheetData><row r="1"><c r="B1"><v>42</v></c></row></sheetData></worksheet>`;
  return buildZip([
    ['xl/workbook.xml', `<workbook ${S}><sheets><sheet name="Sales" sheetId="1" r:id="rId1"/><sheet name="Other" sheetId="2" r:id="rId2"/></sheets></workbook>`],
    ['xl/_rels/workbook.xml.rels', rels(['rId1', 'worksheets/sheet1.xml'], ['rId2', '/xl/worksheets/sheet2.xml'])],
    ['xl/sharedStrings.xml', `<sst ${S}><si><t>Item</t></si><si><t>Price</t></si><si><r><t>Wid</t></r><r><t>get</t></r></si></sst>`],
    ['xl/worksheets/sheet1.xml', sheet1], ['xl/worksheets/sheet2.xml', sheet2]]);
}

const XLSX_MD = `## Sales

| Item | Price | Ok |
| --- | --- | --- |
| Widget | 3.5 | TRUE |
| xy | 7 | FALSE |

## Other

| 42 |
| --- |
`;

function makePptx() {
  const sp = (ph: string | null, ...paras: [string, number][]) =>
    '<p:sp><p:nvSpPr><p:cNvPr id="1" name=""/><p:cNvSpPr/>' + (ph ? `<p:nvPr><p:ph type="${ph}"/></p:nvPr>` : '<p:nvPr/>') +
    '</p:nvSpPr><p:txBody>' + paras.map(([t, l]) => '<a:p>' + (l > 0 ? `<a:pPr lvl="${l}"/>` : '') + `<a:r><a:t>${t}</a:t></a:r></a:p>`).join('') + '</p:txBody></p:sp>';
  const slide = (...shapes: string[]) => `<p:sld ${P}><p:cSld><p:spTree>${shapes.join('')}</p:spTree></p:cSld></p:sld>`;
  return buildZip([
    ['ppt/presentation.xml', `<p:presentation ${P}><p:sldIdLst><p:sldId id="256" r:id="rId2"/><p:sldId id="257" r:id="rId3"/></p:sldIdLst></p:presentation>`],
    ['ppt/_rels/presentation.xml.rels', rels(['rId2', 'slides/slide1.xml'], ['rId3', 'slides/slide2.xml'])],
    ['ppt/slides/slide1.xml', slide(sp('title', ['Q3 Results', 0]), sp('body', ['Revenue up', 0], ['Mostly EMEA', 1]), sp('sldNum', ['7', 0]))],
    ['ppt/slides/_rels/slide1.xml.rels', rels(['rId1', '../notesSlides/notesSlide1.xml'])],
    ['ppt/notesSlides/notesSlide1.xml', slide(sp('sldImg'), sp('body', ['Mention the budget', 0]))],
    ['ppt/slides/slide2.xml', slide(sp(null, ['Just a text box', 0]))]]);
}

const PPTX_MD = `## Q3 Results

- Revenue up
   - Mostly EMEA

### Notes

Mention the budget

## Slide 2

Just a text box
`;

describe('converters', () => {
  it('docx', () => expect(toMarkdown(makeDocx(), 'r.docx')).toBe(DOCX_MD));
  it('xlsx', () => expect(toMarkdown(makeXlsx(), 'r.xlsx')).toBe(XLSX_MD));
  it('pptx', () => expect(toMarkdown(makePptx(), 'r.pptx')).toBe(PPTX_MD));
  it('takes the format from magic bytes when the extension is unknown', () => expect(toMarkdown(makeDocx(), 'download.bin')).toBe(DOCX_MD));

  it('html', () => {
    const page = `<html><head><title>T</title><style>p{color:red}</style><script>var x="<p>no</p>";</script></head>
<body><nav><a href="/">Home</a></nav><h1>Main &amp; Title</h1>
<p>Some <strong>bold</strong>, <em>italic</em> and <a href="https://x.org/a">a link</a>.<br>Second line</p>
<ul><li>One<ul><li>Nested <code>x()</code></li></ul></li><li>Two</li></ul>
<ol><li>First</li><li>Second</li></ol>
<pre><code>def f():
    return 1</code></pre>
<table><tr><th>Name</th><th>Qty</th></tr><tr><td><b>Pen</b></td><td>2</td></tr></table>
<img src="chart.png" alt="Chart"></body></html>`;
    expect(toMarkdown(enc(page), 'page.html')).toBe(`# Main & Title

Some **bold**, *italic* and [a link](https://x.org/a).
Second line

- One
   - Nested \`x()\`
- Two

1. First
2. Second

\`\`\`
def f():
    return 1
\`\`\`

| Name | Qty |
| --- | --- |
| **Pen** | 2 |

![Chart](chart.png)
`);
  });

  it('html survives hostile shapes without hanging', () => {
    expect(toMarkdown(enc('<p>a < b <b>c</p>'), 'x.html')).toContain('a < b');
    expect(toMarkdown(enc('<a href="unterminated>text'), 'x.html')).toContain('text');
    expect(toMarkdown(enc('<div>'.repeat(50_000) + 'deep'), 'x.html')).toBe('deep\n');
    const many = '<a "'.repeat(20_000);
    const started = Date.now();
    toMarkdown(enc(many), 'x.html');
    expect(Date.now() - started).toBeLessThan(20_000); // generous: a loaded machine runs slow
  }, 30_000);

  it('plain formats', () => {
    expect(toMarkdown(enc('name,note\nA,"x, y"\nB,"multi\nline"\n'), 'd.csv')).toBe('| name | note |\n| --- | --- |\n| A | x, y |\n| B | multi line |\n');
    expect(toMarkdown(enc('a\tb\n1\t2\n'), 'd.tsv')).toBe('| a | b |\n| --- | --- |\n| 1 | 2 |\n');
    expect(toMarkdown(enc('{"a":[1,2],"b":"é"}'), 'd.json')).toBe('```json\n{\n  "a": [\n    1,\n    2\n  ],\n  "b": "é"\n}\n```\n');
    expect(toMarkdown(enc('# Hi\n\ntext\n'), 'x.md')).toBe('# Hi\n\ntext\n');
    expect(toMarkdown(enc('plain'), 'x.txt')).toBe('plain');
    expect(toMarkdown(enc('[1]'), 'noext')).toContain('```json');
  });

  it('malformed input is a ShellError, never anything else', () => {
    const png = Uint8Array.from([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a, 0, 1, 2, 0xff]);
    const cases: [Uint8Array, string][] = [
      [enc('PK\x03\x04 not really a zip'), 'x.docx'],
      [buildZip([['word/document.xml', '<w:document><unclosed']]), 'x.docx'],
      [buildZip([['other.xml', '<a/>']]), 'x.xlsx'],
      [buildZip([['ppt/presentation.xml', "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///etc/passwd'>]><x>&e;</x>"]]), 'x.pptx'],
      [buildZip([['word/document.xml', `<w:document ${W}><w:body>&nope;</w:body></w:document>`]]), 'x.docx'],
      [enc('{not json'), 'x.json'],
      [png, 'img.png'],
      [Uint8Array.from([0, 1]), 'noext'],
    ];
    for (const [data, name] of cases) {
      let error: unknown;
      try { toMarkdown(data, name); } catch (e) { error = e; }
      expect(error, name).toBeInstanceOf(Error);
      expect((error as Error).constructor.name, name).toBe('ShellError');
    }
    expect(() => toMarkdown(png, 'img.png')).toThrow(/supported: \.docx/);
    expect(() => toMarkdown(buildZip([['ppt/presentation.xml', '<!DOCTYPE x><x/>']]), 'x.pptx')).toThrow(/DTDs are not allowed/);
  });

  it('caps what a zip may expand to', () => {
    const saved = converterLimits.maxUncompressedBytes;
    converterLimits.maxUncompressedBytes = 1000;
    try {
      const bomb = buildZip([['word/document.xml', `<w:document ${W}><w:body>${' '.repeat(5000)}</w:body></w:document>`]]);
      expect(() => toMarkdown(bomb, 'x.docx')).toThrow(/more than/);
    } finally { converterLimits.maxUncompressedBytes = saved; }
  });

  it('refuses PDF without starting anything', () => {
    expect(() => toMarkdown(enc('%PDF-1.4 hi'), 'x.pdf')).toThrow(/PDF/);
  });
});

describe('to-markdown command', () => {
  const docShell = () => {
    const sh = new Shell();
    registerDocuments(sh);
    sh.fs.writeBytes('/home/agent/report.docx', makeDocx());
    sh.fs.writeBytes('/home/agent/data.xlsx', makeXlsx());
    return sh;
  };

  it('pipes, writes -o, and labels several files', async () => {
    const sh = docShell();
    expect((await sh.run('to-markdown report.docx | grep -n Revenue')).stdout).toBe('5:Revenue was **record** and *growing*. See [the site](https://example.com/)\n');
    expect((await sh.run('to-markdown -o out.md report.docx')).exitCode).toBe(0);
    expect(sh.fs.readText('/home/agent/out.md')).toBe(DOCX_MD);
    expect((await sh.run("to-markdown report.docx data.xlsx | grep '^# '")).stdout).toBe('# report.docx\n# Annual Report\n# Overview\n# data.xlsx\n');
    expect((await sh.run('help')).stdout).toContain('to-markdown');
  });

  it('reports errors per file and keeps going', async () => {
    const sh = docShell();
    const r = await sh.run('to-markdown missing.docx report.docx');
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toBe('to-markdown: missing.docx: No such file or directory\n');
    expect(r.stdout).toContain('# Annual Report');
    expect((await sh.run('to-markdown')).exitCode).toBe(1);
    sh.fs.writeText('/home/agent/bad.docx', 'not a zip');
    const bad = await sh.run('to-markdown bad.docx');
    expect(bad.exitCode).toBe(1);
    expect(bad.stderr.startsWith('to-markdown: bad.docx: not a valid docx file')).toBe(true);
  });

  it('respects file permissions', async () => {
    const sh = docShell();
    sh.fs.writeBytes('/home/agent/secret.docx', makeDocx());
    sh.fs.chmod('/home/agent/secret.docx', 0);
    sh.fs.setOwner('/home/agent/secret.docx', 'root');
    const r = await sh.run('to-markdown secret.docx');
    expect(r.exitCode).toBe(1);
    expect(r.stderr).toContain('secret.docx: Permission denied');
  });

  it('addDocument mounts bytes, optionally with a Markdown twin', async () => {
    const sh = docShell();
    const bytes = makeDocx();
    addDocument(sh.fs, '/data/docs/real.docx', bytes, true);
    expect([...sh.fs.readBytes('/data/docs/real.docx')]).toEqual([...bytes]);
    expect(sh.fs.readText('/data/docs/real.docx.md')).toBe(DOCX_MD);
    addDocument(sh.fs, '/data/raw.xlsx', makeXlsx());
    expect(sh.fs.exists('/data/raw.xlsx.md')).toBe(false);
    expect((await sh.run('to-markdown /data/raw.xlsx | head -1')).stdout).toBe('## Sales\n');
  });
});
