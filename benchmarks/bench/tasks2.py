"""Round 2 tasks (README, "Round 2"): five over large documents and twelve harder shell tasks.

Fixtures come from fixtures/generate2.py, built in memory from a seed. Expected values are computed here from the
generated data. Every task has a reference bash solution; the self-test runs it on arm A (Topwater) and arm Cp (real
bash with python3). A reference that fails on Topwater is a finding, recorded in `a_gap`, not a broken task.
"""
import base64
import collections
import datetime as dt
import json
import re
import sys
from pathlib import Path

from .tasks import TASKS, Task, csv_rows, has, has_num, ok, p, rows_match

sys.path.insert(0, str(Path(__file__).resolve().parent.parent / "fixtures"))
from generate2 import build  # noqa: E402

BIG, HARD = "A,B,Bc,Cp", "A,Cp"


def data(tid):
    return build(tid)[2]


def T2(tid, category, prompt, check, reference, arms, a_gap="", expected="", a_reference=""):
    return Task(tid, category, prompt, check, expected, reference, round=2, arms=arms, a_gap=a_gap, a_reference=a_reference,
                files=lambda: build(tid)[0], setup=(lambda: build(tid)[1]) if build(tid)[1] else None)


def commits(c, repo="~/proj"):
    """{subject: full hash} in the episode's repository."""
    out = c.run(f"cd {repo} && git log --all --format='%H|%s'")
    return {s: h for h, _, s in (line.partition("|") for line in out.splitlines() if "|" in line)}


# ---------- large documents ----------
def check_orders(c):
    rows = data("x_orders_xlsx")["rows"]
    rev, bad = collections.Counter(), []
    for oid, _, _, cat, _, q, price in rows:
        if q > 1000:
            bad.append(oid)
        else:
            rev[cat] += q * price
    got = csv_rows(c.read(p("~/out/category_revenue.csv")))
    want = [["category", "revenue"]] + [[k, f"{v:.2f}"] for k, v in rev.most_common()]
    ids = set(re.findall(r"ORD-\d{6}", c.final))
    return ok(rows_match(got, want, numeric_cols=(1,)) and ids == set(bad), f"rows {got!r:.200} ids {sorted(ids)}")


def log_facts():
    n, e, ip = collections.Counter(), collections.Counter(), collections.Counter()
    for line in data("x_access_log")["lines"]:
        f = line.split()
        h = f[0][11:13]
        n[h] += 1
        if f[4].startswith("5"):
            e[h] += 1
            ip[f[1]] += 1
    rates = sorted(((e[h] / n[h], h) for h in n), reverse=True)
    assert rates[0][0] > rates[1][0] * 1.2 and ip.most_common(2)[0][1] > ip.most_common(2)[1][1]
    return n, e, rates[0][1], ip.most_common(1)[0][0]


def check_log(c):
    n, e, peak, top = log_facts()
    got = csv_rows(c.read(p("~/out/hourly.csv")))
    want = [["hour", "requests", "errors"]] + [[h, str(n[h]), str(e[h])] for h in sorted(n)]
    return ok(rows_match(got, want) and has_num(c.final, int(peak), 0) and top in c.final, f"rows {got!r:.200} final {c.final[:120]!r}")


# ---------- harder shell tasks ----------
def gross(net, disc=0):
    net = net * (100 - disc) // 100
    return (net * 121 + 50) // 100


def check_merge(c):
    parents = c.run("cd ~/proj && git log -1 --format=%P main").split()
    dirty = c.run("cd ~/proj && git status --porcelain").strip()
    log = c.run("cd ~/proj && git show main:CHANGELOG.md") + c.run("cd ~/proj && git show main:src/pricing.sh")
    cases = [(10000, 0), (10000, 10), (999, 5), (1234, 0), (5, 50)]
    got = c.run("cd ~/proj && git show main:src/pricing.sh > /tmp/.p.sh && bash -c 'source /tmp/.p.sh; "
                + "; ".join(f"gross {n} {d}" if d else f"gross {n}" for n, d in cases) + "'").split()
    want = [str(gross(n, d)) for n, d in cases]
    good = (len(parents) == 2 and not dirty and "<<<<<<<" not in log and ">>>>>>>" not in log and "optional discount" in log
            and "VAT is 21%" in log and got == want)
    return ok(good, f"parents={len(parents)} dirty={dirty[:60]!r} gross={got} want={want}")


def check_patch(c):
    d = data("h_patch_drift")
    head = c.run("cd ~/proj && git log -1 --format='%an|%s'").strip()
    body = c.run("cd ~/proj && git show HEAD:lib/retry.sh")
    count = c.run("cd ~/proj && git rev-list --count HEAD").strip()
    dirty = c.run("cd ~/proj && git status --porcelain").strip()
    return ok(head == f"{d['author']}|{d['subject']}" and body == d["want"] and count == "3" and not dirty,
              f"head={head!r} body ok={body == d['want']} count={count} dirty={dirty[:60]!r}")


def check_pickaxe(c):
    d, h = data("h_pickaxe"), commits(c)
    f = c.final.lower()
    added, removed = h.get(d["added"][0], "?" * 40), h.get(d["removed"][0], "?" * 40)
    return ok(added[:7] in f and removed[:7] in f and has(f, "Tomasz", "Priya"), f"final={c.final[:160]!r} want {added[:7]} {removed[:7]}")


def colstat_line(texts, col, delim):
    vals = [int(line.split(delim)[col - 1]) for t in texts for line in t.splitlines()[1:] if line]
    return f"count={len(vals)} sum={sum(vals)} min={min(vals)} max={max(vals)}"


def check_colstat(c):
    d = data("h_colstat_script")
    hidden = base64.b64encode(d["hidden"].encode()).decode()
    out = c.run("cd ~ && ~/bin/colstat -c 3 ~/data/a.csv; echo \"rc=$?\"; ~/bin/colstat -d \"$(printf '\\t')\" -c 2 ~/data/b.tsv ~/data/c.tsv; echo \"rc=$?\"; "
                f"printf %s {hidden} | base64 -d > /tmp/.h.txt; ~/bin/colstat -d ';' -c 2 /tmp/.h.txt; echo \"rc=$?\"; "
                "~/bin/colstat /tmp/.h.txt 2>/dev/null; echo \"rc=$?\"; ~/bin/colstat -c 2 2>/dev/null; echo \"rc=$?\"; "
                "~/bin/colstat -c 2 ~/data/a.csv /nonexistent 2>/dev/null; echo \"rc=$?\"").split("\n")
    one, two = colstat_line([d["a"]], 3, ","), colstat_line([d["b"], d["c"]], 2, "\t")
    want = [one, "rc=0", two, "rc=0", colstat_line([d["hidden"]], 2, ";"), "rc=0", "rc=2", "rc=2", "rc=1", ""]
    got = [x.strip() for x in out]
    answered = all(has_num(c.final, int(x.split("=")[1]), 0) for x in (one.split()[1], two.split()[1]))
    return ok(got == want and answered, f"got {got} want {want} answered={answered}")


def payroll():
    d = data("h_join_payroll")
    dept = dict(d["depts"])
    emp = {e: dep for e, _, dep in d["emps"]}
    n, s = collections.Counter(), collections.Counter()
    for e, sal in d["sal"]:
        n[dept[emp[e]]] += 1
        s[dept[emp[e]]] += sal
    rows = sorted(s, key=lambda k: -s[k])
    assert len(set(s.values())) == len(s)
    return [[k, str(n[k]), str(s[k])] for k in rows]


def check_payroll(c):
    text = c.read(p("~/out/payroll.tsv"))
    got = [line.split("\t") for line in (text or "").strip().split("\n")] if text else None
    return ok(got == payroll(), f"got {got!r:.300}")


def add_months(d, m):
    y, mo = divmod(d.month - 1 + m, 12)
    return d.replace(year=d.year + y, month=mo + 1)


def notices():
    today, end = dt.date(2026, 11, 26), dt.date(2027, 1, 31)
    out = []
    for cid, _, start, term, notice in data("h_notice_dates")["rows"]:
        s, k = dt.date.fromisoformat(start), 1
        while add_months(s, k * term) < today:
            k += 1
        r = add_months(s, k * term)
        dl = r - dt.timedelta(days=notice)
        if today <= dl <= end:
            out.append([cid, r.isoformat(), dl.isoformat()])
    out.sort(key=lambda x: (x[2], x[0]))
    assert 5 <= len(out) <= 30
    return [["id", "renewal_date", "notice_deadline"]] + out


def check_notices(c):
    got = csv_rows(c.read(p("~/out/notice_deadlines.csv")))
    return ok(got == notices(), f"got {got!r:.300}")


def photo_facts():
    moved, kept = [], []
    for f in data("h_photo_sort")["files"]:
        name = f.rsplit("/", 1)[1]
        if re.search(r"\.jpe?g$", name, re.I):
            moved.append(f"photos/{name[4:8]}/{name[8:10]}/{name}")
        else:
            kept.append(f)
    return sorted(moved + kept), len(moved)


def check_photos(c):
    want, n = photo_facts()
    got = sorted(x for x in c.run("cd ~ && find photos inbox -type f").split("\n") if x)
    empty = c.run("cd ~ && find inbox -mindepth 1 -type d -empty").strip()
    return ok(got == want and not empty and has_num(c.final, n, 0), f"files match={got == want} ({len(got)} vs {len(want)}) empty={empty[:80]!r} final={c.final[:60]!r}")


def site_facts():
    want, changed = {}, 0
    for path, text in data("h_site_domain")["files"].items():
        new = text
        if not path.startswith("site/archive/") and path.endswith((".md", ".html")):
            new = text.replace("http://old.example.org/", "https://docs.example.com/")
            new = "".join(line.replace("© 2025", "© 2026") if line.startswith("<footer") else line for line in new.splitlines(True))
        changed += new != text
        want[path] = new
    return want, changed


def check_site(c):
    want, n = site_facts()
    bad = [k for k, v in want.items() if c.read(p("~/" + k)) != v]
    return ok(not bad and has_num(c.final, n, 0), f"wrong files {bad[:4]} final={c.final[:60]!r}")


def ranking():
    def secs(t):
        m, s = t.split(":")
        return int(m) * 60 + float(s)
    rows = sorted(data("h_sort_keys")["rows"], key=lambda x: (x[1], -int(x[3]), secs(x[4]), x[0]))
    keys = [(x[1], x[3], x[4], x[0]) for x in rows]
    assert len(set(keys)) == len(keys)
    return [["name", "country", "event", "points", "time"]] + rows


def check_ranking(c):
    text = c.read(p("~/out/ranking.tsv"))
    got = [line.split("\t") for line in (text or "").strip().split("\n")] if text else None
    return ok(got == ranking(), f"got {got!r:.200}")


def ledger():
    bal, neg = collections.Counter(), set()
    for e in sorted(data("h_ledger_jq")["events"], key=lambda e: e["ts"]):
        if e["type"] == "deposit":
            bal[e["account"]] += e["amount_cents"]
        elif e["type"] == "withdrawal":
            bal[e["account"]] -= e["amount_cents"]
        else:
            bal[e["from"]] -= e["amount_cents"]
            bal[e["to"]] += e["amount_cents"]
        neg |= {k for k, v in bal.items() if v < 0}
    assert 0 < len(neg) < len(bal)
    return dict(sorted(bal.items())), sorted(neg)


def check_ledger(c):
    bal, neg = ledger()
    try:
        got = json.loads(c.read(p("~/out/balances.json")) or "null")
    except ValueError:
        return False, "not JSON"
    named = sorted(set(re.findall(r"AC-\d+", c.final)))
    return ok(got == bal and named == neg, f"balances match={got == bal} named={named} want={neg}")


TAR_WANT = {"app-1.2/bin/run.sh": "-rwxr-xr-x", "app-1.2/conf/secret.env": "-rw-------", "app-1.2/conf/app.env": "-rw-r--r--",
            "app-1.2/lib/util.sh": "-rw-r--r--", "app-1.2/lib/db.sh": "-rw-r--r--", "app-1.2/README.md": "-rw-r--r--"}


def check_tar(c):
    got = {}
    for line in c.run("tar tzvf ~/release/app-1.2.tar.gz 2>/dev/null").splitlines():
        f = line.split()
        if f and f[0].startswith("-"):
            got[re.sub(r"^\./", "", f[-1])] = f[0]
    return ok(got == TAR_WANT, f"got {got}")


def check_bisect(c):
    subject = data("h_bisect")["first_bad"]
    h = commits(c).get(subject, "?" * 40)
    branch = c.run("cd ~/proj && git rev-parse --abbrev-ref HEAD").strip()
    return ok(h[:7] in c.final.lower() and has(c.final, subject) and branch == "main", f"final={c.final[:120]!r} want {h[:7]} branch={branch!r}")


_n, _e, PEAK, TOP_IP = log_facts()
TASKS2 = [
    # --- large documents (H4)
    T2("x_contract_clause", "bigdoc", "Under the agreement in ~/big/msa.docx, how much does the Supplier owe for each calendar day that a delivery "
       "milestone is late, and which clause says so?",
       lambda c: ok(has_num(c.final, 1850, 0) and "31.7" in c.final, "amount and clause"),
       """line=$(to-markdown ~/big/msa.docx | grep 'for each calendar day of delay'); echo "ANSWER: clause ${line%% *}, $(grep -o 'EUR [0-9,]*' <<<"$line") per day" """,
       BIG, expected="EUR 1,850 per day, clause 31.7"),
    T2("x_orders_xlsx", "bigdoc", "~/big/orders-2026.xlsx has one row per order line. Rows with a quantity over 1000 are data-entry errors: leave them out, "
       "and tell me their order IDs. Write ~/out/category_revenue.csv with the header category,revenue: revenue is quantity x unit_price summed per "
       "category, to 2 decimals, highest first.", check_orders,
       r"""mkdir -p ~/out; to-markdown ~/big/orders-2026.xlsx | awk -F' *[|] *' '$2 ~ /^ORD-/ { if ($7 > 1000) bad = bad " " $2; else r[$5] += $7 * $8 }
         END { for (c in r) printf "%s,%.2f\n", c, r[c] > "/tmp/rev"; print "ANSWER:" bad }'
         { echo category,revenue; sort -t, -k2,2nr /tmp/rev; } > ~/out/category_revenue.csv""", BIG),
    T2("x_deck_slide", "bigdoc", "Which slide of ~/big/qbr-2026.pptx states the board-approved FY2027 customer churn target, and what is the target?",
       lambda c: ok(has_num(c.final, 117, 0) and has_num(c.final, 3.8) and not has_num(c.final, 4.2), "slide and target"),
       """to-markdown ~/big/qbr-2026.pptx | awk '/^## Slide/ {s = $3} /board-approved/ {print "ANSWER: slide " s ": " $0}'""",
       BIG, expected="slide 117, 3.8%"),
    T2("x_access_log", "bigdoc", "~/big/access.log is one day of web server access log (timestamp, client IP, method, path, status, duration). Write "
       "~/out/hourly.csv with the header hour,requests,errors and one row per hour (00 to 23, in order), where errors counts responses with a 5xx "
       "status. Then tell me which hour had the highest error rate (errors / requests) and which client IP received the most 5xx responses.",
       check_log,
       r"""mkdir -p ~/out; awk '{h = substr($1, 12, 2); n[h]++; if ($5 ~ /^5/) {e[h]++; ip[$2]++}}
         END {for (k in n) printf "%s,%d,%d\n", k, n[k], e[k] + 0 > "/tmp/h"; for (i in ip) print ip[i], i > "/tmp/ip"}' ~/big/access.log
         { echo hour,requests,errors; sort /tmp/h; } > ~/out/hourly.csv
         peak=$(awk -F, '{print $3 / $2, $1}' /tmp/h | sort -rn | head -1 | cut -d' ' -f2); top=$(sort -rn /tmp/ip | head -1 | cut -d' ' -f2)
         echo "ANSWER: hour $peak, $top" """, BIG),
    T2("x_supplier_search", "bigdoc", "One of the supplier agreements in ~/big/suppliers/ includes a data processing addendum. Which supplier is it, and how "
       "many days' notice does that agreement require for termination?",
       lambda c: ok(has(c.final, "Ironbridge") and has_num(c.final, 75, 0) and not has(c.final, "Garnet"), "supplier and notice"),
       r"""cd ~/big/suppliers && for f in *.docx; do t=$(to-markdown "$f"); if grep -q '^## Data Processing Addendum' <<<"$t"; then
         echo "ANSWER: $(sed -n 's/^# Supplier agreement: //p' <<<"$t") ($f), $(grep -o '[0-9]* days' <<<"$t")"; fi; done""",
       BIG, expected="Ironbridge IT (renewal 2025), 75 days"),
    # --- harder shell work (H2)
    T2("h_merge_conflict", "hard", "In the git repository ~/proj, merge the branch feature/discounts into main. The merge conflicts. Resolve it so that "
       "src/pricing.sh keeps main's 21% VAT and half-up rounding and also takes the branch's optional discount argument, applied to the net price first "
       "the way the branch does it, and so that CHANGELOG.md lists both changes. Then commit the merge.", check_merge,
       r"""cd ~/proj && git merge -q feature/discounts >/dev/null 2>&1; cat > src/pricing.sh <<'EOF'
VAT_PCT=21

# gross NET_CENTS [DISCOUNT_PCT]: the price including VAT, in cents, after an optional discount, rounded half up
gross() {
  local net=$1 disc=${2:-0}
  net=$(( net * (100 - disc) / 100 ))
  echo $(( (net * (100 + VAT_PCT) + 50) / 100 ))
}
EOF
printf '# Changes\n\n- Pricing helper\n- gross takes an optional discount\n- VAT is 21%% and gross rounds half up\n' > CHANGELOG.md
git add -A && git commit -q --no-edit""", HARD),
    T2("h_patch_drift", "hard", "~/patches holds a patch made with git format-patch for the repository ~/proj. Apply it to ~/proj as a commit that keeps "
       "the patch's author and commit message.", check_patch, "cd ~/proj && git am -3 -q ~/patches/*.patch", HARD, a_gap="Topwater's git has no `am`",
       a_reference="""cd ~/proj && sed -i -e 's/^  local i$/  local i  # attempt number, from 1/' -e 's/^    sleep 1$/    sleep $(( 2 ** (i - 1) ))/' lib/retry.sh &&
          git add lib/retry.sh && git commit -q --author='Jonas Feld <jonas@example.test>' -m 'retry: back off exponentially between attempts'"""),
    T2("h_pickaxe", "hard", "The setting LEGACY_TOKEN_TTL is no longer anywhere in the repository ~/proj, but it used to be. Which commit first added it, "
       "and which commit removed it? Give each commit's short hash and author.", check_pickaxe,
       """cd ~/proj && a=$(git log --reverse --format='%h %an' -S LEGACY_TOKEN_TTL | head -1); b=$(git log --format='%h %an' -S LEGACY_TOKEN_TTL | head -1)
          echo "ANSWER: added in $a; removed in $b" """, HARD, a_gap="Topwater's git log has no -S (nor rev-list --reverse)",
       a_reference="""echo "ANSWER: $(cd ~/proj && for c in $(git rev-list HEAD | tac); do git show $c | grep -q '^[-+]LEGACY_TOKEN_TTL' && git log -1 --format='%h %an' $c; done | tr '\\n' ';')" """),
    T2("h_colstat_script", "hard", "Write an executable bash script ~/bin/colstat. Usage: `colstat [-d DELIM] -c COLUMN FILE...`. It skips the first line "
       "(a header) of each FILE and, over the remaining lines of all the files together, prints one line `count=N sum=S min=MIN max=MAX` for column "
       "COLUMN (counting from 1; the values are integers). DELIM defaults to a comma. Parse the options with getopts. If -c is missing or no FILE is "
       "given, print a usage message to stderr and exit with status 2. If a FILE does not exist, print an error to stderr and exit with status 1 "
       "without printing the summary. Remove any temporary file with a trap. Then run it on column 3 of ~/data/a.csv, and on column 2 of "
       "~/data/b.tsv and ~/data/c.tsv together (tab-separated), and give me both output lines.", check_colstat,
       r"""mkdir -p ~/bin && cat > ~/bin/colstat <<'EOF'
#!/bin/bash
usage() { echo 'usage: colstat [-d DELIM] -c COLUMN FILE...' >&2; exit 2; }
d=, c=
while getopts 'd:c:' o; do case $o in d) d=$OPTARG ;; c) c=$OPTARG ;; *) usage ;; esac; done
shift $((OPTIND - 1))
[ -n "$c" ] && [ $# -gt 0 ] || usage
tmp=$(mktemp); trap 'rm -f "$tmp"' EXIT
for f; do [ -f "$f" ] || { echo "colstat: $f: no such file" >&2; exit 1; }; tail -n +2 "$f" >> "$tmp"; done
awk -F"$d" -v c="$c" 'NR == 1 {mn = mx = $c} {s += $c; if ($c < mn) mn = $c; if ($c > mx) mx = $c} END {printf "count=%d sum=%d min=%d max=%d\n", NR, s, mn, mx}' "$tmp"
EOF
chmod +x ~/bin/colstat; echo "ANSWER: $(~/bin/colstat -c 3 ~/data/a.csv); $(~/bin/colstat -d "$(printf '\t')" -c 2 ~/data/b.tsv ~/data/c.tsv)" """, HARD),
    T2("h_join_payroll", "hard", "~/hr has three tab-separated files without headers: employees.tsv (employee id, name, department id), departments.tsv "
       "(department id, department name) and salaries.tsv (employee id, annual salary). Not every employee has a salary row. Write ~/out/payroll.tsv, "
       "tab-separated without a header, one line per department: department name, the number of its employees who have a salary row, and the sum of "
       "their salaries. Leave out departments with none. Sort by the salary sum, highest first.", check_payroll,
       r"""mkdir -p ~/out && cd ~/hr && T=$(printf '\t')
         join -t "$T" <(sort employees.tsv) <(sort salaries.tsv) | cut -f3,4 | sort > /tmp/ds
         join -t "$T" /tmp/ds <(sort departments.tsv) | awk -F'\t' -v OFS='\t' '{n[$3]++; s[$3] += $2} END {for (d in n) print d, n[d], s[d]}' |
           sort -t "$T" -k3,3nr > ~/out/payroll.tsv""", HARD, a_gap="Topwater has no `join`",
       a_reference="""mkdir -p ~/out && cd ~/hr && awk -F'\t' -v OFS='\t' 'FILENAME == "departments.tsv" {d[$1] = $2; next} FILENAME == "employees.tsv" {e[$1] = $3; next}
          {n[d[e[$1]]]++; s[d[e[$1]]] += $2} END {for (k in n) print k, n[k], s[k]}' departments.tsv employees.tsv salaries.tsv | sort -t "$(printf '\t')" -k3,3nr > ~/out/payroll.tsv"""),
    T2("h_notice_dates", "hard", "Each contract in ~/data/contracts.csv renews automatically every term_months months, counted from its start_date. Notice "
       "of cancellation must be given notice_days days before a renewal date. Today is 2026-11-26. For each contract take its next renewal date on or "
       "after today, and the notice deadline that goes with it. Write ~/out/notice_deadlines.csv with the header id,renewal_date,notice_deadline, "
       "listing the contracts whose notice deadline falls between 2026-11-26 and 2027-01-31 inclusive, sorted by notice deadline and then id. "
       "Dates as YYYY-MM-DD.", check_notices,
       r"""mkdir -p ~/out; today=2026-11-26
         tail -n +2 ~/data/contracts.csv | while IFS=, read -r id cust start term notice; do
           k=1; while r=$(date -d "$start + $((k * term)) months" +%F); [[ $r < $today ]]; do k=$((k + 1)); done
           dl=$(date -d "$r - $notice days" +%F)
           [[ ! $dl < $today && ! $dl > 2027-01-31 ]] && echo "$dl,$id,$r"
         done | sort | awk -F, -v OFS=, 'BEGIN {print "id,renewal_date,notice_deadline"} {print $2, $3, $1}' > ~/out/notice_deadlines.csv""", HARD),
    T2("h_photo_sort", "hard", "Move every JPEG file (.jpg or .jpeg, in any letter case) anywhere under ~/inbox into ~/photos/YYYY/MM/, using the date in its "
       "file name (IMG_YYYYMMDD_...), and keep the file name. Some names contain spaces. Leave all other files where they are, then remove the "
       "directories under ~/inbox that are left empty. How many files did you move?", check_photos,
       r"""cd ~ && n=0; while IFS= read -r -d '' f; do b=$(basename "$f"); d=photos/${b:4:4}/${b:8:2}; mkdir -p "$d"; mv "$f" "$d/"; n=$((n + 1)); done \
         < <(find inbox -type f \( -iname '*.jpg' -o -iname '*.jpeg' \) -print0); find inbox -mindepth 1 -type d -empty -delete; echo "ANSWER: $n" """, HARD),
    T2("h_site_domain", "hard", "Under ~/site, in every .md and .html file except those under ~/site/archive/, change links to the old documentation host, "
       "http://old.example.org/<path>, to https://docs.example.com/<path>; links to any other host stay as they are. In the same .html files, also change "
       "'© 2025' to '© 2026' on footer lines (lines starting with <footer) and nowhere else. Leave .txt files and everything under ~/site/archive/ "
       "untouched. How many files did you change?", check_site,
       r"""cd ~/site && files=$(find . -path ./archive -prune -o -type f \( -name '*.md' -o -name '*.html' \) -print | xargs grep -l -e 'http://old\.example\.org/' -e '^<footer.*© 2025')
         sed -i -e 's#http://old\.example\.org/#https://docs.example.com/#g' -e '/^<footer/s/© 2025/© 2026/' $files; echo "ANSWER: $(echo "$files" | wc -l)" """, HARD),
    T2("h_sort_keys", "hard", "Write ~/out/ranking.tsv with the same header and rows as ~/data/results.tsv, sorted by country (A to Z), then points "
       "(highest first), then time (fastest first; times are minutes:seconds), then name (A to Z).", check_ranking,
       r"""mkdir -p ~/out; T=$(printf '\t'); { head -1 ~/data/results.tsv; tail -n +2 ~/data/results.tsv | awk -F'\t' -v OFS='\t' '{split($5, t, ":"); print t[1] * 60 + t[2], $0}' |
         sort -t "$T" -k3,3 -k5,5nr -k1,1n -k2,2 | cut -f2-; } > ~/out/ranking.tsv""", HARD),
    T2("h_ledger_jq", "hard", "~/data/events.json lists account events (deposits, withdrawals and transfers from one account to another) in no particular "
       "order. Every account starts at 0. Write ~/out/balances.json: one JSON object that maps each account id to its final balance in cents, keys "
       "sorted. Then replay the events in time order (ts) and tell me which accounts were ever below zero.", check_ledger,
       r"""mkdir -p ~/out; step='if $e.type == "deposit" then .[$e.account] = (.[$e.account] // 0) + $e.amount_cents
           elif $e.type == "withdrawal" then .[$e.account] = (.[$e.account] // 0) - $e.amount_cents
           else .[$e.from] = (.[$e.from] // 0) - $e.amount_cents | .[$e.to] = (.[$e.to] // 0) + $e.amount_cents end'
         jq -S "reduce .[] as \$e ({}; $step)" ~/data/events.json > ~/out/balances.json
         neg=$(jq -r "sort_by(.ts) | reduce .[] as \$e ({b: {}, neg: {}}; .b |= ($step) | .neg += (.b | with_entries(select(.value < 0)))) | .neg | keys | join(\" \")" ~/data/events.json)
         echo "ANSWER: $neg" """, HARD),
    T2("h_release_tar", "hard", "Package ~/app as ~/release/app-1.2.tar.gz, a gzip-compressed tar archive. Inside it, everything must be under a top-level "
       "directory app-1.2/ (not app/). Leave out the cache/ directory and every *.bak file. In the archive, bin/run.sh must have mode 0755, "
       "conf/secret.env mode 0600 and every other file mode 0644.", check_tar,
       """rm -rf /tmp/stage && mkdir -p /tmp/stage ~/release && cp -r ~/app /tmp/stage/app-1.2 && cd /tmp/stage && rm -rf app-1.2/cache &&
          find app-1.2 -name '*.bak' -delete && find app-1.2 -type f -exec chmod 644 {} + && chmod 755 app-1.2/bin/run.sh &&
          chmod 600 app-1.2/conf/secret.env && tar czf ~/release/app-1.2.tar.gz app-1.2""", HARD),
    T2("h_bisect", "hard", "In the git repository ~/proj, `bash check.sh` passes at the tag v1.0 but fails on main. Find the first commit at which it fails "
       "and give its short hash and subject. Leave main checked out when you are done.", check_bisect,
       """cd ~/proj && git bisect start main v1.0 >/dev/null && git bisect run bash check.sh >/dev/null 2>&1; c=$(git rev-parse refs/bisect/bad)
          git bisect reset >/dev/null 2>&1; echo "ANSWER: $(git log -1 --format='%h %s' "$c")" """, HARD, a_gap="Topwater's git has no `bisect` (nor rev-list --reverse)",
       a_reference="""cd ~/proj && for c in $(git rev-list v1.0..main | tac); do git checkout -q $c; bash check.sh >/dev/null || break; done
          git checkout -q main; echo "ANSWER: $(git log -1 --format='%h %s' $c)" """),
]

ALL = TASKS + TASKS2
BY_ID = {t.id: t for t in ALL}
assert len(BY_ID) == len(ALL)
ROUND1 = [t for t in ALL if t.round == 1]
ROUND2 = [t for t in ALL if t.round == 2]
