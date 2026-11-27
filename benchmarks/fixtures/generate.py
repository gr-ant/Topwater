"""Writes the benchmark's fixtures into this folder, deterministically (seeded). Run once; the output is committed.

    python3 benchmarks/fixtures/generate.py
"""
import csv
import io
import json
import random
import zipfile
from pathlib import Path
from xml.sax.saxutils import escape

HERE = Path(__file__).parent
R = random.Random(2026)

# ---------- the shop's database ----------
first = ["Alder", "Birch", "Cedar", "Delta", "Ember", "Fjord", "Granite", "Harbor", "Iris", "Juniper", "Kestrel", "Lumen",
         "Maple", "Nimbus", "Orchid", "Pioneer", "Quarry", "Raven", "Summit", "Tundra"]
second = ["Works", "Supply", "Labs", "Traders", "Goods", "Collective", "Partners", "Outfitters"]
countries = ["DE", "FR", "US", "UK", "NL", "ES"]
customers = []
names = R.sample([f"{a} {b}" for a in first for b in second], 40)
for i, name in enumerate(names, 1):
    customers.append({"id": f"C-{i:04d}", "name": name, "country": R.choice(countries),
                      "tier": R.choices(["bronze", "silver", "gold"], [5, 3, 2])[0],
                      "email": name.lower().replace(" ", ".") + "@example.com",
                      "joined": f"2025-{R.randint(1, 12):02d}-{R.randint(1, 28):02d}"})

categories = {"tools": ["Claw Hammer", "Torque Wrench", "Cordless Drill", "Utility Knife", "Spirit Level", "Hex Key Set"],
              "garden": ["Pruning Shears", "Hose Reel", "Seed Tray", "Rain Barrel", "Leaf Rake", "Trowel"],
              "kitchen": ["Chef Knife", "Cast Iron Pan", "Spice Rack", "Cutting Board", "Kettle", "Colander"],
              "lighting": ["Desk Lamp", "LED Strip", "Lantern", "Floor Lamp", "Bulb Pack", "Night Light"]}
products = []
n = 101
for cat, items in categories.items():
    for item in items:
        products.append({"sku": f"SKU-{n}", "name": item, "category": cat,
                         "price": round(R.uniform(6, 180), 2), "reorder_point": R.choice([10, 15, 20, 25, 30])})
        n += 1
warehouses = ["BER", "LYO", "NYC"]
inventory = [{"sku": p["sku"], "warehouse": w, "on_hand": R.randint(0, 22)} for p in products for w in warehouses]

orders = []
for i in range(320):
    day = R.randint(0, 142)  # 2026-07-01 .. 2026-11-20
    month_days = [31, 31, 30, 31, 30]
    m, d = 7, day
    for md in month_days:
        if d < md:
            break
        d -= md
        m += 1
    placed = f"2026-{m:02d}-{d + 1:02d}"
    items = []
    for p in R.sample(products, R.choice([1, 1, 2, 2, 3])):
        items.append({"sku": p["sku"], "qty": R.randint(1, 5), "unit_price": p["price"]})
    status = R.choices(["open", "shipped", "delivered", "cancelled", "refunded"], [3, 3, 8, 1.5, 1])[0]
    if m == 11 and d > 12 and status in ("delivered", "refunded"):
        status = "shipped"
    orders.append({"id": f"O-{1001 + i}", "customer_id": R.choice(customers)["id"], "placed_on": placed, "status": status,
                   "total": round(sum(it["qty"] * it["unit_price"] for it in items), 2), "items": items})
orders.sort(key=lambda o: (o["placed_on"], o["id"]))

carriers = ["DHL", "UPS", "FedEx", "PostNL"]
shipments = []
for o in orders:
    if o["status"] in ("shipped", "delivered", "refunded"):
        y, mo, dd = map(int, o["placed_on"].split("-"))
        sd = min(dd + R.randint(1, 3), 28)
        shipments.append({"id": f"S-{5001 + len(shipments)}", "order_id": o["id"], "carrier": R.choices(carriers, [4, 3, 2, 2])[0],
                          "shipped_on": f"{y}-{mo:02d}-{sd:02d}",
                          "status": "in_transit" if o["status"] == "shipped" else "delivered"})

(HERE / "shop.json").write_text(json.dumps({"customers": customers, "products": products, "inventory": inventory,
                                            "orders": orders, "shipments": shipments}, indent=1) + "\n")

# ---------- files in the agent's home ----------
home = HERE / "home"
for sub in ["data", "logs", "config", "docs", "scripts", "tmp", "repo_src"]:
    (home / sub).mkdir(parents=True, exist_ok=True)

regions = ["north", "south", "east", "west"]
reps = {"north": ["ana", "ben"], "south": ["cai", "dee"], "east": ["eli", "fay"], "west": ["gus", "hal"]}
buf = io.StringIO()
w = csv.writer(buf, lineterminator="\n")
w.writerow(["date", "region", "rep", "sku", "units", "unit_price"])
for _ in range(600):
    reg = R.choice(regions)
    p = R.choice(products)
    w.writerow([f"2026-{R.randint(1, 10):02d}-{R.randint(1, 28):02d}", reg, R.choice(reps[reg]), p["sku"], R.randint(1, 12), p["price"]])
(home / "data/sales_2026.csv").write_text(buf.getvalue())

users = []
for i in range(1, 61):
    nm = R.choice(first) + " " + R.choice(["Ng", "Okafor", "Silva", "Kowalski", "Haddad", "Larsen", "Moreau", "Tanaka", "Brennan", "Vasquez"])
    users.append({"id": f"u{i:03d}", "name": nm, "email": f"user{i:03d}@corp.example", "role": R.choices(["viewer", "editor", "admin"], [6, 3, 1])[0],
                  "active": R.random() > 0.25, "last_login": f"2026-{R.randint(8, 11):02d}-{R.randint(1, 28):02d}", "tags": R.sample(["beta", "sso", "mfa", "legacy"], R.randint(0, 2))})
(home / "data/users.json").write_text(json.dumps(users, indent=2) + "\n")

buf = io.StringIO()
w = csv.writer(buf, lineterminator="\n")
w.writerow(["name", "email", "company", "phone"])
contacts = []
for i in range(68):
    nm = R.choice(first) + " " + R.choice(["Ng", "Okafor", "Silva", "Kowalski", "Haddad", "Larsen", "Moreau", "Tanaka"])
    contacts.append([nm, f"{nm.split()[0].lower()}.{i}@mail.example", R.choice(names), f"+49 30 {R.randint(100000, 999999)}"])
rows = list(contacts)
for c in R.sample(contacts, 12):  # duplicates: the same email in another case, later in the file
    dup = list(c)
    dup[1] = dup[1].upper() if R.random() < 0.5 else dup[1].capitalize()
    rows.insert(R.randint(contacts.index(c) + 1, len(rows)), dup)
for r in rows:
    w.writerow(r)
(home / "data/contacts.csv").write_text(buf.getvalue())

buf = io.StringIO()
w = csv.writer(buf, lineterminator="\n")
w.writerow(["sku", "name", "category", "on_hand", "unit_cost"])
for p in products:
    w.writerow([p["sku"], p["name"], p["category"], sum(i["on_hand"] for i in inventory if i["sku"] == p["sku"]), round(p["price"] * 0.55, 2)])
(home / "data/inventory.csv").write_text(buf.getvalue())

services = ["api", "auth", "worker", "billing"]
lines = []
t = 10 * 3600
for i in range(900):
    t += R.randint(1, 20)
    ts = f"2026-11-20T{t // 3600:02d}:{t % 3600 // 60:02d}:{t % 60:02d}Z"
    svc = R.choices(services, [5, 2, 2, 1])[0]
    u = f"u{R.randint(1, 60):03d}"
    roll = R.random()
    if svc == "auth" and roll < 0.35:
        lines.append(f"{ts} WARN  [auth] login failed user={u} ip=10.0.{R.randint(0, 9)}.{R.randint(1, 250)}")
    elif roll < 0.06 + (0.08 if svc == "billing" else 0):
        lines.append(f"{ts} ERROR [{svc}] req={R.randrange(16**6):06x} {R.choice(['timeout talking to db', 'null customer id', 'upstream 502', 'disk quota exceeded'])}")
    else:
        lines.append(f"{ts} INFO  [{svc}] req={R.randrange(16**6):06x} user={u} {R.choice(['GET', 'GET', 'POST'])} /{R.choice(['orders', 'cart', 'search', 'profile'])} "
                     f"{R.choices([200, 201, 404, 500], [20, 3, 2, 1])[0]} {int(R.lognormvariate(4.6, 0.8))}ms")
# u023 is the target of a password-guessing burst
for k in range(9):
    t += 2
    lines.append(f"2026-11-20T{t // 3600:02d}:{t % 3600 // 60:02d}:{t % 60:02d}Z WARN  [auth] login failed user=u023 ip=203.0.113.9")
(home / "logs/app.log.txt").write_text("\n".join(lines) + "\n")   # placed as ~/logs/app.log (the repo ignores *.log)

ini = {"api.ini": "[server]\nhost = 0.0.0.0\nport = 8080\nworkers = 4\n\n[db]\nurl = postgres://db:5432/shop\npool = 10\n",
       "gateway.ini": "[server]\nhost = 0.0.0.0\nport = 8443\ntls = on\n\n[upstream]\napi = http://api:8080\ntimeout = 15\n",
       "worker.ini": "[queue]\nurl = redis://cache:6379/0\nconcurrency = 8\n\n[server]\nport = 9100\n",
       "legacy.ini": "; old settings, kept for reference\n[server]\n; port = 8443\nport = 8000\n"}
for k, v in ini.items():
    (home / "config" / k).write_text(v)

(home / ".gitconfig").write_text("[user]\n\tname = Agent\n\temail = agent@sandbox.example\n")
(home / "scripts/cleanup.sh").write_text(
    "#!/bin/bash\n# Remove temporary files from ~/tmp and say how many went.\nn=0\nfor f in \"$HOME\"/tmp/*.tmp; do\n"
    "  [ -e \"$f\" ] || continue\n  rm -- \"$f\"\n  n=$((n + 1))\ndone\necho \"cleanup: removed $n temporary files\"\n")
for name in ["a1.tmp", "b2.tmp", "c3.tmp", "keep.txt"]:
    (home / "tmp" / name).write_text(f"scratch {name}\n")

# ---------- the git repository, as a series of commits applied by a setup script ----------
repo_commits = [
    ("Mara Lindqvist", "2026-03-02T09:15:00Z", "Initial commit", {
        "README.md": "# Ledger service\n\nKeeps the books for the shop. Clients recieve a statement every month.\n",
        "src/app.py": "from config import TIMEOUT\n\n\ndef main():\n    print('ledger up, timeout', TIMEOUT)\n",
        "src/config.py": "TIMEOUT = 10\nRETRIES = 3\n",
    }),
    ("Mara Lindqvist", "2026-03-05T14:02:00Z", "Add billing module", {
        "src/billing.py": "def invoice_total(lines):\n    return sum(q * p for q, p in lines)\n",
    }),
    ("Tomasz Wierzbicki", "2026-03-11T10:40:00Z", "Billing: apply VAT", {
        "src/billing.py": "VAT = 0.19\n\n\ndef invoice_total(lines):\n    net = sum(q * p for q, p in lines)\n    return round(net * (1 + VAT), 2)\n",
    }),
    ("Priya Raman", "2026-04-01T08:30:00Z", "Docs: user guide", {
        "docs/guide.md": "# Guide\n\nYou will recieve an email when the ledger closes.\nRun `python src/app.py`.\n",
    }),
    ("Mara Lindqvist", "2026-04-20T16:45:00Z", "Release 1.0", {"VERSION": "1.0\n"}),
    ("Tomasz Wierzbicki", "2026-05-14T11:05:00Z", "Raise default timeout", {
        "src/config.py": "TIMEOUT = 30\nRETRIES = 3\n",
    }),
    ("Priya Raman", "2026-06-02T13:20:00Z", "Billing: discounts for gold customers", {
        "src/billing.py": "VAT = 0.19\n\n\ndef invoice_total(lines, discount=0.0):\n    net = sum(q * p for q, p in lines) * (1 - discount)\n"
                          "    return round(net * (1 + VAT), 2)\n\n\ndef on_paid(invoice):\n    # customers recieve a receipt\n    return f'paid {invoice}'\n",
    }),
    ("Mara Lindqvist", "2026-06-30T09:00:00Z", "Release 1.1", {"VERSION": "1.1\n"}),
    ("Jonas Feld", "2026-07-08T15:30:00Z", "Retry failed payments", {
        "src/config.py": "TIMEOUT = 30\nRETRIES = 5\n",
        "src/billing.py": "VAT = 0.19\n\n\ndef invoice_total(lines, discount=0.0):\n    net = sum(q * p for q, p in lines) * (1 - discount)\n"
                          "    return round(net * (1 + VAT), 2)\n\n\ndef on_paid(invoice):\n    # customers recieve a receipt\n    return f'paid {invoice}'\n\n\n"
                          "def retry(payment, attempts):\n    return [payment] * attempts\n",
    }),
]
tags = {"Release 1.0": "v1.0", "Release 1.1": "v1.1"}
script = ["set -e", "mkdir -p ~/repo && cd ~/repo", "git init -q -b main", "git config user.email dev@ledger.example"]
for i, (author, date, msg, files) in enumerate(repo_commits):
    for path, content in files.items():
        src = f"c{i}/{path}"
        (home / "repo_src" / src).parent.mkdir(parents=True, exist_ok=True)
        (home / "repo_src" / src).write_text(content)
        script.append(f"mkdir -p {Path(path).parent} && cp ~/repo_src/{src} {path}")
    script.append(f"git add -A && GIT_AUTHOR_NAME='{author}' GIT_COMMITTER_NAME='{author}' GIT_AUTHOR_DATE={date} GIT_COMMITTER_DATE={date} "
                  f"git -c user.name='{author}' commit -q -m '{msg}'")
    if msg in tags:
        script.append(f"git tag {tags[msg]}")
script.append("rm -rf ~/repo_src")
(HERE / "setup_repo.sh").write_text("\n".join(script) + "\n")


# ---------- Office documents, written as minimal OOXML ----------
def zipped(parts: dict) -> bytes:
    out = io.BytesIO()
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for name, text in parts.items():
            z.writestr(zipfile.ZipInfo(name, (2026, 1, 1, 0, 0, 0)), text)
    return out.getvalue()


W = 'xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"'


def docx(blocks) -> bytes:
    body = []
    for b in blocks:
        if b[0] == "h":
            body.append(f'<w:p><w:pPr><w:pStyle w:val="Heading{b[1]}"/></w:pPr><w:r><w:t>{escape(b[2])}</w:t></w:r></w:p>')
        elif b[0] == "p":
            body.append(f'<w:p><w:r><w:t xml:space="preserve">{escape(b[1])}</w:t></w:r></w:p>')
        elif b[0] == "t":
            rows = "".join("<w:tr>" + "".join(f"<w:tc><w:p><w:r><w:t>{escape(c)}</w:t></w:r></w:p></w:tc>" for c in r) + "</w:tr>" for r in b[1])
            body.append(f"<w:tbl>{rows}</w:tbl>")
    return zipped({
        "[Content_Types].xml": '<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                               '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                               '<Default Extension="xml" ContentType="application/xml"/>'
                               '<Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>',
        "_rels/.rels": '<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                       '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>',
        "word/document.xml": f'<?xml version="1.0" encoding="UTF-8"?><w:document {W}><w:body>{"".join(body)}</w:body></w:document>',
    })


def xlsx(sheets: dict) -> bytes:
    def cell(ref, v):
        if isinstance(v, (int, float)):
            return f'<c r="{ref}"><v>{v}</v></c>'
        return f'<c r="{ref}" t="inlineStr"><is><t>{escape(str(v))}</t></is></c>'
    parts = {}
    sheet_list, rels, overrides = [], [], []
    for i, (name, rows) in enumerate(sheets.items(), 1):
        xml_rows = "".join(f'<row r="{r}">' + "".join(cell(f"{chr(65 + c)}{r}", v) for c, v in enumerate(row)) + "</row>" for r, row in enumerate(rows, 1))
        parts[f"xl/worksheets/sheet{i}.xml"] = ('<?xml version="1.0" encoding="UTF-8"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">'
                                                f"<sheetData>{xml_rows}</sheetData></worksheet>")
        sheet_list.append(f'<sheet name="{escape(name)}" sheetId="{i}" r:id="rId{i}"/>')
        rels.append(f'<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet{i}.xml"/>')
        overrides.append(f'<Override PartName="/xl/worksheets/sheet{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>')
    parts["xl/workbook.xml"] = ('<?xml version="1.0" encoding="UTF-8"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" '
                                'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">'
                                f'<sheets>{"".join(sheet_list)}</sheets></workbook>')
    parts["xl/_rels/workbook.xml.rels"] = f'<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{"".join(rels)}</Relationships>'
    parts["[Content_Types].xml"] = ('<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                                    '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                                    '<Default Extension="xml" ContentType="application/xml"/>'
                                    '<Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>'
                                    f'{"".join(overrides)}</Types>')
    parts["_rels/.rels"] = ('<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>')
    return zipped(parts)


def pptx(slides) -> bytes:
    P = 'xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" ' \
        'xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"'
    parts, ids, rels, overrides = {}, [], [], []
    for i, (title, bullets) in enumerate(slides, 1):
        def shape(n, paras):
            ps = "".join(f"<a:p><a:r><a:t>{escape(t)}</a:t></a:r></a:p>" for t in paras)
            return f'<p:sp><p:nvSpPr><p:cNvPr id="{n}" name="s{n}"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/>{ps}</p:txBody></p:sp>'
        parts[f"ppt/slides/slide{i}.xml"] = f'<?xml version="1.0" encoding="UTF-8"?><p:sld {P}><p:cSld><p:spTree>{shape(2, [title])}{shape(3, bullets)}</p:spTree></p:cSld></p:sld>'
        ids.append(f'<p:sldId id="{255 + i}" r:id="rId{i}"/>')
        rels.append(f'<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide{i}.xml"/>')
        overrides.append(f'<Override PartName="/ppt/slides/slide{i}.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.slide+xml"/>')
    parts["ppt/presentation.xml"] = f'<?xml version="1.0" encoding="UTF-8"?><p:presentation {P}><p:sldIdLst>{"".join(ids)}</p:sldIdLst></p:presentation>'
    parts["ppt/_rels/presentation.xml.rels"] = f'<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{"".join(rels)}</Relationships>'
    parts["[Content_Types].xml"] = ('<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">'
                                    '<Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>'
                                    '<Default Extension="xml" ContentType="application/xml"/>'
                                    '<Override PartName="/ppt/presentation.xml" ContentType="application/vnd.openxmlformats-officedocument.presentationml.presentation.main+xml"/>'
                                    f'{"".join(overrides)}</Types>')
    parts["_rels/.rels"] = ('<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">'
                            '<Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="ppt/presentation.xml"/></Relationships>')
    return zipped(parts)


(home / "docs/travel-policy.docx").write_bytes(docx([
    ("h", 1, "Travel and Expenses Policy"),
    ("p", "This policy applies to all staff travelling on company business from 1 September 2026."),
    ("h", 2, "Accommodation"),
    ("p", "Book hotels through the travel desk. The nightly limits below include taxes; breakfast may be claimed separately."),
    ("t", [["City band", "Nightly limit (EUR)"], ["Band A (London, Paris, New York)", "240"], ["Band B (Berlin, Madrid, Amsterdam)", "175"], ["Band C (all other cities)", "130"]]),
    ("h", 2, "Meals"),
    ("p", "Meals are reimbursed up to 55 EUR per day. Alcohol is never reimbursable."),
    ("h", 2, "Approvals"),
    ("p", "Trips longer than 5 working days need approval from a director before booking."),
]))
(home / "docs/meeting-notes.docx").write_bytes(docx([
    ("h", 1, "Ops sync, 18 November 2026"),
    ("p", "Attendees: Mara, Tomasz, Priya, Jonas."),
    ("h", 2, "Discussion"),
    ("p", "Carrier delays in Lyon continue. The billing retry change shipped last week."),
    ("h", 2, "Action items"),
    ("p", "ACTION: Tomasz to renegotiate the PostNL contract by 2026-12-05."),
    ("p", "ACTION: Priya to publish the updated travel policy on the intranet."),
    ("p", "Note: the warehouse audit moved to January."),
    ("p", "ACTION: Jonas to add alerting for failed payment retries."),
    ("p", "ACTION: Mara to close the October books by Friday."),
]))
depts = [("Engineering", 410000, 438500), ("Marketing", 150000, 171200), ("Sales", 220000, 214900), ("Support", 95000, 101300), ("Finance", 80000, 76400), ("Operations", 130000, 152950)]
(home / "docs/budget-2026.xlsx").write_bytes(xlsx({
    "Q3": [["Department", "Budget", "Actual"]] + [[d, b, a] for d, b, a in depts],
    "Notes": [["Figures in EUR. Actuals as of 2026-10-05."]],
}))
(home / "docs/roadmap.pptx").write_bytes(pptx([
    ("Product roadmap 2027", ["Draft for the planning offsite"]),
    ("Q1 2027", ["Self-service returns portal (owner: Priya)", "Warehouse scanner app (owner: Jonas)"]),
    ("Q2 2027", ["Subscription orders (owner: Tomasz)", "Multi-currency invoices (owner: Mara)"]),
    ("Q3 2027", ["Marketplace integration (owner: Jonas)"]),
]))
print("fixtures written")
