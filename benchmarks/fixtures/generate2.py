"""Round 2 fixtures, built in memory from a seed every time they are needed, so the large ones never enter the
repository. Each task gets its own files (paths relative to the agent's home) and, for the git tasks, a setup script
that builds the repository inside the episode's machine. The raw data comes back too, so the checkers in
bench/tasks2.py compute their expected values from what was generated, not from typed-in numbers.

    python3 benchmarks/fixtures/generate2.py OUTDIR     # write every task's files to OUTDIR/<task>/ to look at them
"""
import csv
import datetime as dt
import difflib
import functools
import hashlib
import io
import json
import random
import sys
import zipfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from ooxml import docx, pptx, xlsx  # noqa: E402

WORDS = ("party agreement service supplier customer obligation term notice period schedule delivery acceptance payment invoice "
         "liability warranty data security audit records personnel subcontractor change request milestone deliverable fee "
         "confidential information intellectual property licence termination breach remedy dispute escalation governance "
         "report review approval reasonable written prior consent applicable law regulation standard quality performance").split()


def _filler(r, n):
    """n words of contract-like prose, in sentences."""
    out, words = [], [r.choice(WORDS) for _ in range(n)]
    for i in range(0, n, 14):
        s = " ".join(words[i:i + 14])
        out.append(s[0].upper() + s[1:] + ".")
    return " ".join(out)


def _repo(steps, where="proj"):
    """steps: ("commit", author, iso date, message, {path: text or None to delete}) or ("sh", command).
    Returns (seed files, setup script). The script builds ~/<where> from the seed files and removes them."""
    files, lines = {}, ["set -e", f"mkdir -p ~/{where} && cd ~/{where}", "git init -q -b main", "git config user.email dev@example.test", "git config user.name 'Ledger Dev'"]
    n = 0
    for s in steps:
        if s[0] == "sh":
            lines.append(s[1])
            continue
        _, author, date, msg, change = s
        n += 1
        for path, text in change.items():
            if text is None:
                lines.append(f"git rm -q {path}")
            else:
                files[f".seed/{n}/{path}"] = text.encode()
                d = path.rsplit("/", 1)[0] if "/" in path else "."
                lines.append(f"mkdir -p {d} && cp ~/.seed/{n}/{path} {path}")
        lines.append(f"git add -A && GIT_AUTHOR_NAME='{author}' GIT_COMMITTER_NAME='{author}' GIT_AUTHOR_DATE={date} GIT_COMMITTER_DATE={date} "
                     f"git -c user.name='{author}' commit -q -m '{msg}'")
    lines.append("rm -rf ~/.seed")
    return files, "\n".join(lines) + "\n"


def _deflate(data):
    """ooxml.zipped stores parts uncompressed (round 1's fixtures depend on its exact bytes); compress the big ones."""
    src, out = zipfile.ZipFile(io.BytesIO(data)), io.BytesIO()
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for info in src.infolist():
            data = src.read(info)
            info.compress_type = zipfile.ZIP_DEFLATED
            z.writestr(info, data)
    return out.getvalue()


def _blob(text):
    data = text.encode()
    return hashlib.sha1(b"blob %d\0" % len(data) + data).hexdigest()


# ---------- large documents ----------
def contract():
    r = random.Random(101)
    titles = ["Definitions", "Term", "Services", "Service Levels", "Personnel", "Subcontracting", "Charges", "Invoicing", "Payment",
              "Taxes", "Audit", "Records", "Data Protection", "Security", "Confidentiality", "Intellectual Property", "Warranties",
              "Indemnities", "Limitation of Liability", "Insurance", "Change Control", "Governance", "Reporting", "Dispute Resolution",
              "Force Majeure", "Step-in Rights", "Business Continuity", "Exit Management", "Termination", "Consequences of Termination",
              "Delays and Liquidated Damages", "Acceptance Testing", "Assignment", "Notices", "Waiver", "Severability", "Entire Agreement",
              "Counterparts", "Third Party Rights", "Governing Law"]
    special = {(4, 3): "If Availability falls below the Service Level in any month, the Supplier shall credit the Customer EUR 400 for each full hour "
                       "of Unavailability, up to a maximum of fifteen percent (15%) of the monthly Charges.",
               (9, 5): "Undisputed invoices not paid when due bear interest at 1.5% per month from the due date until payment.",
               (31, 2): "The parties agree that the liquidated damages in clause 31.7 are a genuine pre-estimate of the Customer's loss and not a penalty.",
               (31, 7): "If the Supplier fails to achieve a Delivery Milestone by its Milestone Date, the Supplier shall pay the Customer liquidated "
                        "damages of EUR 1,850 for each calendar day of delay, up to a maximum of ten percent (10%) of the Contract Price.",
               (33, 4): "A draft of this Agreement proposed liquidated damages of EUR 2,500 per day; that figure was not agreed and has no effect."}
    blocks = [("h", 1, "Master Services Agreement"), ("p", "between Ledger Logistics GmbH (the Customer) and Northwind Systems Ltd (the Supplier), dated 3 March 2026.")]
    for a, title in enumerate(titles, 1):
        blocks.append(("h", 2, f"Article {a}. {title}"))
        for c in range(1, 17):
            text = special.get((a, c)) or _filler(r, r.randint(70, 95))
            blocks.append(("p", f"{a}.{c} {text}"))
    words = sum(len(b[-1].split()) for b in blocks)
    return {"big/msa.docx": _deflate(docx(blocks))}, {"words": words, "daily": 1850, "clause": "31.7"}


def orders_xlsx():
    r = random.Random(102)
    regions = ["North", "South", "East", "West", "Central", "Export"]
    cats = {"Fasteners": (0.4, 6), "Hand tools": (8, 60), "Power tools": (45, 320), "Garden": (5, 90), "Lighting": (4, 75),
            "Plumbing": (2, 55), "Electrical": (1.5, 40), "Safety": (3, 48)}
    skus = {c: [f"{c[:2].upper()}-{r.randint(1000, 9999)}" for _ in range(12)] for c in cats}
    prices = {s: round(r.uniform(*cats[c]), 2) for c in cats for s in skus[c]}
    rows = [["order_id", "date", "region", "category", "sku", "quantity", "unit_price"]]
    outliers = set(r.sample(range(40000), 5))
    for i in range(40000):
        c = r.choice(list(cats))
        s = r.choice(skus[c])
        d = dt.date(2026, 1, 1) + dt.timedelta(days=r.randint(0, 333))
        q = r.randint(2000, 9000) if i in outliers else r.randint(1, 20)
        rows.append([f"ORD-{i + 1:06d}", d.isoformat(), r.choice(regions), c, s, q, prices[s]])
    return {"big/orders-2026.xlsx": _deflate(xlsx({"Orders": rows}))}, {"rows": rows[1:]}


def deck():
    r = random.Random(103)
    regions = ["DACH", "Benelux", "Nordics", "France", "Iberia", "UK & Ireland", "Italy", "Poland"]
    topics = ["Revenue", "Pipeline", "Customer health", "Hiring", "Operations", "Partner channel", "Product adoption", "Support"]
    slides = [("Quarterly business review 2026", ["All regions", "Prepared for the board, November 2026"])]
    while len(slides) < 160:
        reg, top = r.choice(regions), r.choice(topics)
        slides.append((f"{reg}: {top}", [f"{r.choice(['Net revenue', 'Gross margin', 'Bookings', 'Renewals', 'NPS', 'Tickets closed'])}: "
                                         f"{r.randint(2, 98)}{r.choice(['%', ' kEUR', ' accounts', ''])}" for _ in range(r.randint(4, 6))] + [_filler(r, 45)]))
    slides[51] = ("Customer health: planning assumptions", ["FY2026 churn actual: 4.6%", "FY2027 customer churn target (draft, superseded): 4.2%",
                                                            "Expansion revenue share: 31%", "Assumptions to be confirmed by the board in Q4"])
    slides[88] = ("Nordics: Customer health", ["Churn this quarter: 5.1%", "Accounts at risk: 14", "NPS: 41"])
    slides[116] = ("Board decisions, November 2026", ["Hiring plan approved for 2027", "FY2027 customer churn target: 3.8% (board-approved)",
                                                      "Price increase of 4% from 1 February 2027", "Capex envelope unchanged"])
    return {"big/qbr-2026.pptx": _deflate(pptx(slides))}, {"slide": 117, "target": 3.8, "slides": len(slides)}


def access_log():
    r = random.Random(104)
    ips = [f"10.{r.randint(0, 40)}.{r.randint(0, 255)}.{r.randint(1, 254)}" for _ in range(900)]
    bad = ips[417]  # the client behind most 5xx responses
    paths = ["/api/orders", "/api/orders/{}", "/api/customers/{}", "/api/inventory", "/api/shipments/{}", "/health", "/login", "/api/reports"]
    rate = [0.010 + 0.004 * r.random() for _ in range(24)]
    rate[14] = 0.083  # the incident hour
    rate[3] = 0.051
    out, t0 = [], dt.datetime(2026, 11, 2)
    secs = sorted(r.randint(0, 86399) for _ in range(240000))
    for s in secs:
        ts = t0 + dt.timedelta(seconds=s, milliseconds=r.randint(0, 999))
        h = ts.hour
        ip = bad if r.random() < 0.02 else r.choice(ips)
        err = r.random() < (rate[h] * (6 if ip == bad else 1))
        status = r.choice([500, 502, 503, 504]) if err else r.choice([200, 200, 200, 200, 201, 204, 301, 304, 400, 401, 404])
        path = r.choice(paths).format(r.randint(1, 9999))
        out.append(f"{ts:%Y-%m-%dT%H:%M:%S}.{ts.microsecond // 1000:03d}Z {ip} {r.choice(['GET', 'GET', 'GET', 'POST', 'PUT'])} {path} {status} {r.randint(3, 900)}ms")
    return {"big/access.log": ("\n".join(out) + "\n").encode()}, {"lines": out}


def suppliers():
    r = random.Random(105)
    names = ["Abacus Freight", "Birchwood Paper", "Cobalt Couriers", "Dunmore Office", "Elmstead Print", "Foxglove Catering", "Garnet Security",
             "Hawthorn Cleaning", "Ironbridge IT", "Juniper Telecom", "Kingfisher Travel", "Larch Facilities", "Meridian Payroll", "Nettle Recruitment",
             "Oakhurst Legal", "Pinecrest Energy", "Quillon Translations", "Rowan Logistics", "Sorrel Media", "Thistle Software", "Umber Archiving",
             "Vale Water", "Willow Insurance", "Yarrow Events"]
    files, notice = {}, {}
    for i, name in enumerate(names * 2, 1):
        sup = name if i <= len(names) else f"{name} (renewal {2025 if i % 2 else 2026})"
        n = 75 if i == 33 else r.choice([30, 45, 60, 90, 120])
        notice[sup] = n
        blocks = [("h", 1, f"Supplier agreement: {sup}"), ("p", f"Agreement no. SA-{2026}-{i:03d} between Ledger Logistics GmbH and {sup}.")]
        for sec in ["Scope", "Fees", "Term and termination", "Liability", "Confidentiality", "General"]:
            blocks.append(("h", 2, sec))
            for _ in range(r.randint(2, 4)):
                blocks.append(("p", _filler(r, r.randint(40, 70))))
            if sec == "Term and termination":
                blocks.append(("p", f"Either party may terminate this agreement by giving {n} days' written notice."))
        if sup == "Garnet Security":
            blocks.append(("p", "The supplier does not process personal data for the customer, so no data processing addendum is required."))
        if i == 33:  # Ironbridge IT's renewal: the one with the addendum
            blocks += [("h", 2, "Data Processing Addendum"), ("p", "The supplier processes personal data on the customer's behalf as a processor under Article 28 GDPR."),
                       ("p", "Sub-processors need the customer's prior written approval. Personal data is deleted within 30 days after termination.")]
        files[f"big/suppliers/agreement-{i:02d}.docx"] = _deflate(docx(blocks))
    return files, {"supplier": names[32 - len(names)], "file": "agreement-33.docx", "days": 75, "notice": notice}


# ---------- harder shell tasks ----------
BASE_PRICING = "VAT_PCT=19\n\n# gross NET_CENTS: the price including VAT, in cents\ngross() {\n  local net=$1\n  echo $(( net * (100 + VAT_PCT) / 100 ))\n}\n"


def merge_conflict():
    main2 = ("VAT_PCT=21\n\n# gross NET_CENTS: the price including VAT, in cents, rounded half up\ngross() {\n  local net=$1\n"
             "  echo $(( (net * (100 + VAT_PCT) + 50) / 100 ))\n}\n")
    feat = ("VAT_PCT=19\n\n# gross NET_CENTS [DISCOUNT_PCT]: the price including VAT, in cents, after an optional discount\ngross() {\n"
            "  local net=$1 disc=${2:-0}\n  net=$(( net * (100 - disc) / 100 ))\n  echo $(( net * (100 + VAT_PCT) / 100 ))\n}\n")
    files, setup = _repo([
        ("commit", "Mara Lindqvist", "2026-09-01T09:00:00Z", "Pricing helper", {"src/pricing.sh": BASE_PRICING, "CHANGELOG.md": "# Changes\n\n- Pricing helper\n",
                                                                               "README.md": "Pricing tools for the shop.\n"}),
        ("sh", "git checkout -q -b feature/discounts"),
        ("commit", "Tomasz Wierzbicki", "2026-09-08T10:00:00Z", "Discount argument for gross", {"src/pricing.sh": feat, "CHANGELOG.md": "# Changes\n\n- Pricing helper\n- gross takes an optional discount\n"}),
        ("sh", "git checkout -q main"),
        ("commit", "Priya Raman", "2026-09-10T11:00:00Z", "VAT 21 percent, round half up", {"src/pricing.sh": main2, "CHANGELOG.md": "# Changes\n\n- Pricing helper\n- VAT is 21% and gross rounds half up\n"}),
        ("commit", "Priya Raman", "2026-09-11T08:00:00Z", "README: usage", {"README.md": "Pricing tools for the shop.\n\n    source src/pricing.sh; gross 1000\n"}),
    ])
    return files, setup, {}


RETRY_V1 = ("#!/bin/bash\n# retry N CMD...: run CMD up to N times\nretry() {\n  local n=$1; shift\n  local i\n  for ((i = 1; i <= n; i++)); do\n"
            "    \"$@\" && return 0\n    echo \"attempt $i failed\" >&2\n    sleep 1\n  done\n  return 1\n}\n")
RETRY_V2 = RETRY_V1.replace("# retry N CMD...: run CMD up to N times", "# retry N CMD...: run CMD up to N times; returns 1 if every attempt fails")
PATCHED_V1 = RETRY_V1.replace("    sleep 1\n", "    sleep $(( 2 ** (i - 1) ))\n").replace("  local i\n", "  local i  # attempt number, from 1\n")


def patch_drift():
    diff = "".join(difflib.unified_diff(RETRY_V1.splitlines(True), PATCHED_V1.splitlines(True), "a/lib/retry.sh", "b/lib/retry.sh", n=3))
    patch = ("From 3f1c2a9d5e7b8c0a1d2e3f4a5b6c7d8e9f0a1b2c Mon Sep 17 00:00:00 2001\nFrom: Jonas Feld <jonas@example.test>\n"
             "Date: Mon, 5 Oct 2026 14:30:00 +0000\nSubject: [PATCH] retry: back off exponentially between attempts\n\n"
             "Wait 1, 2, 4, ... seconds instead of a fixed second.\n---\n lib/retry.sh | 4 ++--\n 1 file changed, 2 insertions(+), 2 deletions(-)\n\n"
             f"diff --git a/lib/retry.sh b/lib/retry.sh\nindex {_blob(RETRY_V1)[:7]}..{_blob(PATCHED_V1)[:7]} 100755\n"
             + diff.split("\n", 2)[2] + "-- \n2.43.0\n\n")
    files, setup = _repo([
        ("commit", "Mara Lindqvist", "2026-09-20T09:00:00Z", "Add retry helper", {"lib/retry.sh": RETRY_V1, "README.md": "Shell helpers.\n"}),
        ("sh", "chmod 755 lib/retry.sh && git add lib/retry.sh"),
        ("commit", "Priya Raman", "2026-10-12T16:00:00Z", "Document the return value of retry", {"lib/retry.sh": RETRY_V2}),
    ])
    files["patches/0001-retry-back-off-exponentially-between-attempts.patch"] = patch.encode()
    want = RETRY_V2.replace("    sleep 1\n", "    sleep $(( 2 ** (i - 1) ))\n").replace("  local i\n", "  local i  # attempt number, from 1\n")
    return files, setup, {"want": want, "subject": "retry: back off exponentially between attempts", "author": "Jonas Feld"}


def pickaxe():
    r = random.Random(106)
    authors = ["Mara Lindqvist", "Tomasz Wierzbicki", "Priya Raman", "Jonas Feld"]
    auth = "AUTH_PROVIDER=oidc\nSESSION_TTL=1800\n"
    steps, day = [], dt.datetime(2026, 4, 1, 9)
    other = {"src/app.sh": "#!/bin/bash\nsource config/auth.sh\necho up\n", "README.md": "Auth service.\n", "config/auth.sh": auth}
    msgs = ["Initial import"] + [f"{w} {x}" for w, x in zip(r.choices(["Tidy", "Fix", "Update", "Refactor", "Document"], k=30),
                                                            r.choices(["logging", "startup", "README", "session handling", "defaults", "error messages", "tests"], k=30))]
    for i in range(24):
        change = dict(other) if i == 0 else {"src/app.sh": other["src/app.sh"] + f"# rev {i}\n"}
        if i == 8:
            auth += "LEGACY_TOKEN_TTL=3600\n"
            change["config/auth.sh"] = auth
        if i == 12:
            auth = auth.replace("SESSION_TTL=1800", "SESSION_TTL=2400")
            change["config/auth.sh"] = auth
        if i == 17:
            auth = auth.replace("LEGACY_TOKEN_TTL=3600\n", "")
            change["config/auth.sh"] = auth
        steps.append(("commit", authors[(i * 7 + 3) % 4] if i not in (8, 17) else ("Tomasz Wierzbicki" if i == 8 else "Priya Raman"),
                      (day + dt.timedelta(days=3 * i)).strftime("%Y-%m-%dT%H:%M:%SZ"), msgs[i] if i not in (8, 17) else ("Tidy auth defaults" if i == 8 else "Remove unused settings"),
                      change))
    files, setup = _repo(steps)
    return files, setup, {"added": ("Tidy auth defaults", "Tomasz Wierzbicki"), "removed": ("Remove unused settings", "Priya Raman")}


def colstat():
    r = random.Random(107)
    a = "id,item,qty,price\n" + "".join(f"{i},item{i},{r.randint(1, 500)},{r.randint(1, 99)}\n" for i in range(1, 41))
    b = "sku\tunits\tregion\n" + "".join(f"S{i}\t{r.randint(-20, 300)}\tr{i % 3}\n" for i in range(1, 26))
    c = "sku\tunits\tregion\n" + "".join(f"T{i}\t{r.randint(0, 900)}\tr{i % 4}\n" for i in range(1, 18))
    hidden = "a;b;c\n" + "".join(f"{i};{r.randint(-50, 50)};{r.randint(0, 9)}\n" for i in range(1, 12))
    return {"data/a.csv": a.encode(), "data/b.tsv": b.encode(), "data/c.tsv": c.encode()}, "", {"a": a, "b": b, "c": c, "hidden": hidden}


def join3():
    r = random.Random(108)
    depts = [(f"D{n:02d}", d) for n, d in zip(r.sample(range(10, 99), 9), ["Engineering", "Finance", "Legal", "Marketing", "Operations", "Sales",
                                                                          "Support", "Research", "Facilities"])]
    emps = [(f"E{n:04d}", f"{r.choice(['Ada', 'Ben', 'Cleo', 'Dev', 'Eva', 'Finn', 'Gia', 'Hugo', 'Ines', 'Jon'])} {r.choice(['Ng', 'Silva', 'Haddad', 'Larsen', 'Moreau', 'Tanaka'])}",
             r.choice(depts[:8])[0]) for n in r.sample(range(1000, 9999), 120)]
    sal = [(e[0], r.randint(38, 140) * 1000) for e in emps if r.random() > 0.15]
    r.shuffle(sal)
    tsv = lambda rows: "".join("\t".join(map(str, x)) + "\n" for x in rows).encode()  # noqa: E731
    return {"hr/employees.tsv": tsv(emps), "hr/departments.tsv": tsv(depts), "hr/salaries.tsv": tsv(sal)}, "", {"depts": depts, "emps": emps, "sal": sal}


def contracts():
    r = random.Random(109)
    rows = [["id", "customer", "start_date", "term_months", "notice_days"]]
    for i in range(1, 61):
        start = dt.date(r.randint(2021, 2026), r.randint(1, 12), r.randint(1, 28))
        if start > dt.date(2026, 11, 20):
            start = start.replace(year=2025)
        rows.append([f"K-{i:03d}", r.choice(["Alder", "Birch", "Cedar", "Delta", "Ember", "Fjord", "Granite"]) + " " + r.choice(["GmbH", "Ltd", "BV", "SA"]),
                     start.isoformat(), r.choice([1, 3, 6, 12, 12, 24, 36]), r.choice([14, 30, 30, 45, 60, 90])])
    buf = io.StringIO()
    csv.writer(buf, lineterminator="\n").writerows(rows)
    return {"data/contracts.csv": buf.getvalue().encode()}, "", {"rows": rows[1:]}


def photos():
    r = random.Random(110)
    files, dirs = {}, ["inbox", "inbox/phone", "inbox/phone/DCIM 2026", "inbox/camera", "inbox/camera/card 2", "inbox/misc"]
    used = set()
    for i in range(70):
        d = r.choice(dirs)
        day = dt.date(2026, 1, 1) + dt.timedelta(days=r.randint(0, 320))
        stamp = f"{day:%Y%m%d}_{r.randint(0, 235959):06d}"
        kind = r.random()
        if kind < 0.75:
            name = f"IMG_{stamp}{r.choice(['', '', ' (1)', ' copy'])}.{r.choice(['jpg', 'JPG', 'jpeg', 'JPEG', 'Jpg'])}"
        else:
            name = f"{r.choice(['VID', 'notes', 'IMG'])}_{stamp}.{r.choice(['mov', 'txt', 'png', 'heic'])}"
        if name in used:
            continue
        used.add(name)
        files[f"{d}/{name}"] = f"fake image data {i}\n".encode()
    return files, "", {"files": sorted(files)}


def site():
    r = random.Random(111)
    files = {}
    for i in range(48):
        d = r.choice(["site", "site/guide", "site/guide/advanced", "site/blog", "site/archive", "site/archive/2024"])
        ext = r.choice(["md", "md", "html", "html", "txt"])
        lines = [f"Page {i}"]
        for _ in range(r.randint(3, 9)):
            k = r.random()
            if k < 0.3:
                lines.append(f"See http://old.example.org/{r.choice(['guide', 'api', 'faq', 'blog'])}/{r.randint(1, 50)} for details.")
            elif k < 0.4:
                lines.append("Mirror: https://old.example.org.mirror.test/files (leave this one alone)")
            else:
                lines.append(_filler(r, r.randint(6, 14)))
        if ext == "html":
            lines = ["<html><body>"] + [f"<p>{x}</p>" for x in lines] + [f"<footer>© 2025 Ledger Logistics</footer>", "<p>Archive of © 2025 posts</p>", "</body></html>"]
        files[f"{d}/page-{i:02d}.{ext}"] = ("\n".join(lines) + "\n").encode()
    return files, "", {"files": {k: v.decode() for k, v in files.items()}}


def results_tsv():
    r = random.Random(112)
    rows = []
    for _ in range(90):
        name = f"{r.choice(['Ada', 'Bo', 'Cy', 'Di', 'Ed', 'Flo', 'Gus', 'Hal', 'Ivy', 'Jo', 'Kai', 'Liv'])} {r.choice(['Ng', 'Silva', 'Okafor', 'Berg', 'Rossi', 'Kim'])}"
        secs = r.randint(170, 900) + r.randint(0, 9) / 10
        rows.append([name, r.choice(["NOR", "SWE", "FIN", "GER", "ITA", "CAN", "USA"]), r.choice(["5k", "10k"]), str(r.choice([60, 70, 80, 90, 100])),
                     f"{int(secs // 60)}:{secs % 60:04.1f}"])
    rows = [list(x) for x in {tuple(x) for x in rows}]
    rows.sort(key=lambda x: r.random())
    return {"data/results.tsv": ("name\tcountry\tevent\tpoints\ttime\n" + "".join("\t".join(x) + "\n" for x in rows)).encode()}, "", {"rows": rows}


def ledger_events():
    r = random.Random(113)
    accts = [f"AC-{n}" for n in r.sample(range(100, 999), 12)]
    ev, ts = [], dt.datetime(2026, 10, 1)
    for i in range(400):
        ts += dt.timedelta(minutes=r.randint(1, 180))
        k = r.random()
        if k < 0.4:
            e = {"type": "deposit", "account": r.choice(accts), "amount_cents": r.randint(1000, 90000)}
        elif k < 0.75:
            e = {"type": "withdrawal", "account": r.choice(accts), "amount_cents": r.randint(500, 60000)}
        else:
            a, b = r.sample(accts, 2)
            e = {"type": "transfer", "from": a, "to": b, "amount_cents": r.randint(500, 50000)}
        e = {"id": f"ev{i + 1:04d}", "ts": ts.strftime("%Y-%m-%dT%H:%M:%SZ"), **e}
        ev.append(e)
    shuffled = list(ev)
    r.shuffle(shuffled)
    return {"data/events.json": (json.dumps(shuffled, indent=1) + "\n").encode()}, "", {"events": ev}


def app_tree():
    files = {"app/bin/run.sh": b"#!/bin/bash\necho running\n", "app/conf/app.env": b"PORT=8080\n", "app/conf/secret.env": b"API_TOKEN=not-a-real-token\n",
             "app/lib/util.sh": b"util() { :; }\n", "app/lib/util.sh.bak": b"old\n", "app/lib/db.sh": b"db() { :; }\n", "app/cache/a.tmp": b"x\n",
             "app/cache/b.tmp": b"y\n", "app/README.md": b"The app.\n", "app/conf/app.env.bak": b"PORT=80\n"}
    return files, "", {}


def bisect():
    calc_ok = "#!/bin/bash\n# average of integer arguments, rounded down\naverage() {\n  local sum=0 n=0 x\n  for x in \"$@\"; do sum=$((sum + x)); n=$((n + 1)); done\n  echo $((sum / n))\n}\n"
    calc_bad = calc_ok.replace("n=$((n + 1)); done", "n=$((n + 1)); done\n  [ $n -gt 2 ] && n=$((n + 1))")
    check = "#!/bin/bash\nsource lib/calc.sh\n[ \"$(average 2 4 6)\" = 4 ] && [ \"$(average 10)\" = 10 ] && echo PASS || { echo FAIL; exit 1; }\n"
    authors = ["Mara Lindqvist", "Tomasz Wierzbicki", "Priya Raman", "Jonas Feld"]
    subjects = ["Add calc library", "Logging", "Docs", "CI script", "Lint", "Startup banner", "Config file", "README badges", "Cache dir",
                "Faster average for long inputs", "Changelog", "Version 1.1", "Tidy", "Help text", "More docs", "Error messages", "Rename vars",
                "Comments", "Release notes", "Version 1.2"]
    steps, calc = [], calc_ok
    for i, s in enumerate(subjects):
        ch = {"NOTES.md": "".join(f"- {x}\n" for x in subjects[:i + 1])}
        if i == 0:
            ch.update({"lib/calc.sh": calc, "check.sh": check})
        if i == 9:
            calc = calc_bad
            ch["lib/calc.sh"] = calc
        if i in (13, 16):
            calc = calc.replace("# average of integer arguments", "# average of integer arguments" + ("," if i == 13 else " (bash only)"))
            ch["lib/calc.sh"] = calc
        steps.append(("commit", authors[i % 4], f"2026-08-{i + 1:02d}T10:00:00Z", s, ch))
        if i == 3:
            steps.append(("sh", "git tag v1.0"))
    files, setup = _repo(steps)
    return files, setup, {"first_bad": subjects[9]}


BUILDERS = {"x_contract_clause": contract, "x_orders_xlsx": orders_xlsx, "x_deck_slide": deck, "x_access_log": access_log,
            "x_supplier_search": suppliers, "h_merge_conflict": merge_conflict, "h_patch_drift": patch_drift, "h_pickaxe": pickaxe,
            "h_colstat_script": colstat, "h_join_payroll": join3, "h_notice_dates": contracts, "h_photo_sort": photos, "h_site_domain": site,
            "h_sort_keys": results_tsv, "h_ledger_jq": ledger_events, "h_release_tar": app_tree, "h_bisect": bisect}


@functools.lru_cache(maxsize=None)
def build(task_id):
    """(files {path under home: bytes}, setup script, data) for one task; the same every time."""
    out = BUILDERS[task_id]()
    return out if len(out) == 3 else (out[0], "", out[1])


if __name__ == "__main__":
    root = Path(sys.argv[1])
    for tid in BUILDERS:
        files, setup, _ = build(tid)
        for rel, data in files.items():
            (root / tid / rel).parent.mkdir(parents=True, exist_ok=True)
            (root / tid / rel).write_bytes(data)
        if setup:
            (root / tid / "setup.sh").write_text(setup)
        print(f"{tid}: {len(files)} files, {sum(map(len, files.values())):,} bytes")
