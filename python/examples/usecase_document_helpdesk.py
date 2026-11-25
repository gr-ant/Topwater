"""Use case: a helpdesk agent that answers from your real documents.

Drop Word, Excel, PowerPoint and HTML files into the agent's filesystem with `fs.add_document`. The agent
reads them with the built-in `to-markdown` command and searches them like any other text: grep, sed, awk.
No vector database, no chunking pipeline; the documents stay as they are.

    GEMINI_API_KEY=... python examples/usecase_document_helpdesk.py [extra.pdf ...]

The sample documents are generated below so the example is self-contained. Pass your own files to add them
(for PDFs, the host's `pdftotext` is exposed as a command when it is installed; to-markdown does not parse PDF).
"""
from __future__ import annotations

import io
import re
import shutil
import sys
import zipfile
from pathlib import Path
from xml.sax.saxutils import escape

from agent_loop import report, run_agent

from topwater import Shell
from topwater.host import register_host_command

# ---------- tiny builders for real .docx / .xlsx files ----------

_CT = ('<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
       '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
       '<Default Extension="xml" ContentType="application/xml"/>{overrides}</Types>')
_RELS = ('<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
         '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="{target}"/>'
         '</Relationships>')
_W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main"


def make_docx(blocks: list) -> bytes:
    """blocks: ("h1"|"h2"|"p", text) or ("table", [[cell, ...], ...])."""
    body = []
    for kind, content in blocks:
        if kind == "table":
            rows = "".join("<w:tr>" + "".join(f"<w:tc><w:p><w:r><w:t>{escape(str(c))}</w:t></w:r></w:p></w:tc>" for c in row) + "</w:tr>"
                           for row in content)
            body.append(f"<w:tbl>{rows}</w:tbl>")
        else:
            style = {"h1": "Heading1", "h2": "Heading2"}.get(kind)
            ppr = f'<w:pPr><w:pStyle w:val="{style}"/></w:pPr>' if style else ""
            body.append(f'<w:p>{ppr}<w:r><w:t xml:space="preserve">{escape(content)}</w:t></w:r></w:p>')
    doc = f'<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="{_W}"><w:body>{"".join(body)}</w:body></w:document>'
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", _CT.format(overrides='<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>'))
        z.writestr("_rels/.rels", _RELS.format(target="word/document.xml"))
        z.writestr("word/document.xml", doc)
    return buf.getvalue()


def make_xlsx(sheet_name: str, rows: list[list]) -> bytes:
    def cell(v):
        if isinstance(v, (int, float)):
            return f"<c><v>{v}</v></c>"
        return f'<c t="inlineStr"><is><t>{escape(str(v))}</t></is></c>'

    ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main"
    sheet = f'<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="{ns}"><sheetData>' + "".join(
        "<row>" + "".join(cell(v) for v in row) + "</row>" for row in rows) + "</sheetData></worksheet>"
    workbook = (f'<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="{ns}" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
                f'<sheets><sheet name="{escape(sheet_name)}" sheetId="1" r:id="rId1"/></sheets></workbook>')
    wb_rels = ('<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
               '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>'
               '</Relationships>')
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("[Content_Types].xml", _CT.format(overrides=(
            '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
            '<Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>')))
        z.writestr("_rels/.rels", _RELS.format(target="xl/workbook.xml"))
        z.writestr("xl/workbook.xml", workbook)
        z.writestr("xl/_rels/workbook.xml.rels", wb_rels)
        z.writestr("xl/worksheets/sheet1.xml", sheet)
    return buf.getvalue()


# ---------- the knowledge base ----------

TRAVEL_POLICY = make_docx([
    ("h1", "Travel and Expense Policy"),
    ("p", "Effective 1 March 2026. Applies to all employees of Tidepool Labs."),
    ("h2", "Booking"),
    ("p", "Flights must be booked at least 14 days in advance through the travel portal. Business class is permitted only for flights longer than 8 hours."),
    ("h2", "Daily meal allowance"),
    ("p", "Meal allowances depend on the city tier of the destination:"),
    ("table", [["City tier", "Example cities", "Daily meal allowance (USD)"],
               ["Tier 1", "London, New York, Tokyo, Zurich", 95],
               ["Tier 2", "Berlin, Chicago, Toronto", 70],
               ["Tier 3", "All other cities", 55]]),
    ("h2", "Approval"),
    ("p", "Trips costing more than 2,500 USD in total need approval from a director before booking."),
    ("h2", "Receipts"),
    ("p", "Submit receipts within 30 days of returning. Expenses under 25 USD do not need a receipt."),
])

BENEFITS = make_xlsx("Health plans 2026", [
    ["Plan", "Monthly employee cost (USD)", "Annual deductible (USD)", "Dental included"],
    ["Bronze", 0, 3000, "No"],
    ["Silver", 85, 1500, "Yes"],
    ["Gold", 190, 500, "Yes"],
    ["Family Plus", 340, 750, "Yes"],
])

IT_SECURITY = """<!doctype html><html><head><title>IT Security Guide</title><style>body{font:14px sans-serif}</style></head><body>
<nav>Home | Policies | Contact</nav>
<h1>IT Security Guide</h1>
<h2>Passwords</h2>
<ul><li>Minimum length: <strong>14 characters</strong>.</li><li>Rotate every 180 days.</li><li>Use the company password manager.</li></ul>
<h2>Lost or stolen devices</h2>
<p>Report a lost laptop or phone to the service desk within <em>2 hours</em> at <a href="mailto:servicedesk@tidepool.example">servicedesk@tidepool.example</a>
 so it can be wiped remotely.</p>
<h2>Software</h2>
<p>Only install software from the internal catalogue. Requests for new software take up to 5 working days.</p>
<script>console.log("tracking")</script></body></html>"""

QUESTIONS = [
    ("I'm flying to Zurich for 4 days. What is my total meal allowance?", [r"\b380\b"]),
    ("My trip to Tokyo will cost about 3,100 USD. Do I need anyone's approval?", [r"director"]),
    ("Which health plan has the lowest deductible, and what does it cost me per month?", [r"gold", r"\b190\b"]),
    ("How quickly do I have to report a lost laptop, and to whom?", [r"2 hours|two hours", r"servicedesk@tidepool\.example|service desk"]),
    ("How long does my password have to be?", [r"\b14\b"]),
]


def main() -> None:
    shell = Shell()
    if shutil.which("pdftotext"):
        register_host_command(shell, "pdftotext")  # the agent runs `pdftotext -layout FILE.pdf -`
    fs = shell.fs
    kb = "/home/agent/knowledge-base"
    fs.add_document(f"{kb}/travel-and-expense-policy.docx", TRAVEL_POLICY)
    fs.add_document(f"{kb}/health-plans-2026.xlsx", BENEFITS)
    fs.add_document(f"{kb}/it-security-guide.html", IT_SECURITY.encode())
    for extra in sys.argv[1:]:
        fs.add_document(f"{kb}/{Path(extra).name}", extra)
    fs.set_owner("/home/agent/knowledge-base", shell.user, recursive=True)

    prompt = ("You are the employee helpdesk for Tidepool Labs. The company's policy documents are in ~/knowledge-base. "
              "Answer these questions using only those documents, and name the document each answer came from:\n"
              + "\n".join(f"{i}. {q}" for i, (q, _) in enumerate(QUESTIONS, 1)))
    result = run_agent(shell, prompt)

    sections = [s for s in re.split(r"\n(?=\s*(?:#+\s*|\*\*)?\s*[1-5][.)])", "\n" + result.answer) if re.match(r"\s*(?:#+\s*|\*\*)?\s*[1-5][.)]", s)]
    checks = []
    for i, (q, patterns) in enumerate(QUESTIONS, 1):
        part = next((s for s in sections if re.match(rf"\s*(?:#+\s*|\*\*)?\s*{i}[.)]", s)), result.answer)
        checks.append((q, all(re.search(p, part, re.I) for p in patterns)))
    used = any("to-markdown" in c["command"] for c in result.commands)
    checks.append(("agent found and used to-markdown", used))
    ok = report("Document helpdesk", checks, result)
    raise SystemExit(0 if ok else 1)


if __name__ == "__main__":
    main()
