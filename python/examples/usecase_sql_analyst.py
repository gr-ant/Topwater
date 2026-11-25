"""Use case: a data analyst agent over your real database.

The agent gets a read-only `sqlite3` command that talks to a real SQLite database on the host. It explores the
schema, runs queries, pipes results through the usual text tools and writes its report into the virtual filesystem.
It cannot modify the database, and it never touches the host filesystem.

    GEMINI_API_KEY=... python examples/usecase_sql_analyst.py

`SqliteCommand` below is ~100 lines and is the whole integration: it mimics the parts of the sqlite3 CLI that
models reach for (-header, -csv, -json, .tables, .schema, SQL from stdin).
"""
from __future__ import annotations

import csv
import io
import json
import random
import re
import sqlite3
import tempfile
from pathlib import Path

from agent_loop import report, run_agent

from topwater import Shell, ShellError


class SqliteCommand:
    """A read-only `sqlite3` for the agent. Maps paths in the virtual filesystem to real database files."""

    def __init__(self, shell: Shell, databases: dict[str, str]):
        self.databases = {shell.resolve(k): v for k, v in databases.items()}
        for path, real in self.databases.items():
            # the real bytes, so size, `file` and `xxd` all look right (a stub here made a model distrust the machine)
            shell.fs.write_bytes(path, Path(real).read_bytes())
            shell.fs.set_owner(path, shell.user)
        shell.register_command("sqlite3", self, "SQLite database shell (read-only)")

    def __call__(self, ctx) -> int:
        mode, headers, sep = "list", False, "|"
        args = list(ctx.args)
        positional: list[str] = []
        while args:
            a = args.pop(0)
            flag = a.lstrip("-") if a.startswith("-") else None
            if flag in ("header", "headers"):
                headers = True
            elif flag in ("noheader",):
                headers = False
            elif flag in ("csv", "json", "column", "list", "line", "table", "markdown", "box"):
                mode = flag
            elif flag == "separator":
                sep = args.pop(0) if args else sep
            elif flag in ("readonly", "batch", "bail", "echo"):
                pass
            elif flag == "cmd":
                positional.append(args.pop(0) if args else "")
            elif flag == "help":
                ctx.print("Usage: sqlite3 [OPTIONS] FILENAME [SQL]\nFILENAME is the name of an SQLite database.\nOPTIONS include:\n"
                          "   -column              set output mode to 'column'\n   -csv                 set output mode to 'csv'\n"
                          "   -header              turn headers on\n   -json                set output mode to 'json'\n"
                          "   -list                set output mode to 'list'\n   -markdown            set output mode to 'markdown'\n"
                          "   -readonly            open the database read-only\n   -separator SEP       set output column separator. Default: '|'\n"
                          "   -version             show SQLite version")
                return 0
            elif flag == "version":
                ctx.print(f"{sqlite3.sqlite_version} 2024-01-30 16:01:20")
                return 0
            elif flag is not None and flag != "":
                raise ShellError(f"Error: unknown option: {a}\nUse -help for a list of options.", 1)
            else:
                positional.append(a)
        if not positional:
            raise ShellError("usage: sqlite3 [OPTIONS] FILENAME [SQL]", 1)
        db_arg, statements = positional[0], positional[1:]
        real = self.databases.get(ctx.resolve(db_arg))
        if real is None:
            raise ShellError(f'Error: unable to open database "{db_arg}": unable to open database file', 1)
        script = "\n".join(statements) if statements else ctx.stdin
        conn = sqlite3.connect(f"file:{real}?mode=ro", uri=True)
        try:
            for stmt in self._split(script):
                if stmt.startswith("."):
                    parts = stmt.split()
                    cmd = parts[0]
                    if cmd in (".headers", ".header"):
                        headers = len(parts) > 1 and parts[1] == "on"
                    elif cmd == ".mode" and len(parts) > 1:
                        mode = parts[1]
                    elif cmd == ".separator" and len(parts) > 1:
                        sep = parts[1].strip("'\"")
                    elif cmd == ".tables":
                        rows = conn.execute("SELECT name FROM sqlite_master WHERE type IN ('table','view') AND name NOT LIKE 'sqlite_%' ORDER BY 1")
                        ctx.print("  ".join(r[0] for r in rows))
                    elif cmd in (".schema", ".fullschema"):
                        q = "SELECT sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%'"
                        rows = conn.execute(q + " AND name LIKE ? ORDER BY name", (parts[1],)) if len(parts) > 1 else conn.execute(q + " ORDER BY name")
                        for (sql,) in rows:
                            ctx.print(sql + ";")
                    elif cmd in (".quit", ".exit"):
                        break
                    else:
                        ctx.error(f'Error: unknown command or invalid arguments:  "{cmd[1:]}". Enter ".help" for help')
                        return 1
                    continue
                try:
                    cur = conn.execute(stmt)
                except sqlite3.Error as e:
                    msg = str(e)
                    ctx.error(f"Error: in prepare, {msg}" if "syntax" in msg or "no such" in msg else f"Runtime error: {msg}")
                    return 1
                if cur.description:
                    self._emit(ctx, cur, mode, headers, sep)
        finally:
            conn.close()
        return 0

    @staticmethod
    def _split(script: str) -> list[str]:
        """Splits a script into dot-commands and SQL statements (several statements may share a line)."""
        out, buf = [], ""
        for line in script.split("\n"):
            if not buf.strip() and line.strip().startswith("."):
                out.append(line.strip())
                continue
            for ch in line + "\n":
                buf += ch
                if ch == ";" and sqlite3.complete_statement(buf):  # a ';' outside strings/comments ends a statement
                    out.append(buf.strip())
                    buf = ""
        if buf.strip():
            out.append(buf.strip())
        return [s for s in out if s and s != ";"]

    @staticmethod
    def _emit(ctx, cur, mode: str, headers: bool, sep: str) -> None:
        names = [d[0] for d in cur.description]
        rows = cur.fetchall()

        def text(v):
            return "" if v is None else str(v)

        if mode == "json":
            ctx.print(json.dumps([dict(zip(names, r)) for r in rows]))
        elif mode == "csv":
            buf = io.StringIO()
            w = csv.writer(buf, lineterminator="\n")
            if headers:
                w.writerow(names)
            w.writerows(rows)
            ctx.stdout.write(buf.getvalue())
        elif mode in ("column", "table", "box", "markdown"):
            table = [names] + [[text(v) for v in r] for r in rows]
            widths = [max(len(row[i]) for row in table) for i in range(len(names))]
            if mode == "markdown":
                ctx.print("| " + " | ".join(n.ljust(w) for n, w in zip(names, widths)) + " |")
                ctx.print("|" + "|".join("-" * (w + 2) for w in widths) + "|")
                for row in table[1:]:
                    ctx.print("| " + " | ".join(c.ljust(w) for c, w in zip(row, widths)) + " |")
                return
            ctx.print("  ".join(n.ljust(w) for n, w in zip(names, widths)).rstrip())
            ctx.print("  ".join("-" * w for w in widths))
            for row in table[1:]:
                ctx.print("  ".join(c.ljust(w) for c, w in zip(row, widths)).rstrip())
        elif mode == "line":
            width = max(len(n) for n in names)
            for r in rows:
                for n, v in zip(names, r):
                    ctx.print(f"{n.rjust(width)} = {text(v)}")
                ctx.print("")
        else:
            if headers:
                ctx.print(sep.join(names))
            for r in rows:
                ctx.print(sep.join(text(v) for v in r))


# ---------- demo database ----------

def build_database(path: str) -> None:
    rng = random.Random(7)
    conn = sqlite3.connect(path)
    conn.executescript("""
        CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT NOT NULL, region TEXT NOT NULL, signed_up DATE NOT NULL);
        CREATE TABLE products (id INTEGER PRIMARY KEY, name TEXT NOT NULL, category TEXT NOT NULL, price REAL NOT NULL);
        CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES customers(id),
                             ordered_at DATE NOT NULL, status TEXT NOT NULL CHECK (status IN ('paid','refunded','cancelled')));
        CREATE TABLE order_items (order_id INTEGER NOT NULL REFERENCES orders(id), product_id INTEGER NOT NULL REFERENCES products(id),
                                  quantity INTEGER NOT NULL);
    """)
    regions = ["north", "south", "east", "west"]
    names = ["Acme Corp", "Globex", "Initech", "Umbrella", "Hooli", "Stark Industries", "Wayne Enterprises", "Wonka",
             "Cyberdyne", "Soylent", "Tyrell", "Massive Dynamic", "Vandelay", "Dunder Mifflin", "Pied Piper", "Oscorp",
             "Bluth Company", "Sterling Cooper"]  # the last two stopped ordering after 2025
    for i, n in enumerate(names, 1):
        conn.execute("INSERT INTO customers VALUES (?,?,?,?)", (i, n, regions[i % 4], f"2025-{1 + i % 12:02d}-{1 + (i * 3) % 27:02d}"))
    products = [("Widget", "hardware", 19.5), ("Gadget", "hardware", 42.0), ("Gizmo", "hardware", 8.25), ("Pro Plan", "software", 99.0),
                ("Team Plan", "software", 249.0), ("Support Pack", "services", 150.0), ("Training Day", "services", 600.0),
                ("Cable", "accessories", 4.99), ("Case", "accessories", 14.0), ("Dock", "accessories", 79.0)]
    for i, (n, c, p) in enumerate(products, 1):
        conn.execute("INSERT INTO products VALUES (?,?,?,?)", (i, n, c, p))
    for oid in range(1, 401):
        year = 2025 if oid <= 160 else 2026
        cust = rng.randint(1, len(names) if year == 2025 else len(names) - 2)
        month = rng.randint(1, 12)
        items = [(rng.randint(1, len(products)), rng.randint(1, 5)) for _ in range(rng.randint(1, 4))]
        refund_bias = 0.22 if any(products[p - 1][1] == "accessories" for p, _ in items) else 0.06
        roll = rng.random()
        status = "refunded" if roll < refund_bias else "cancelled" if roll > 0.95 else "paid"
        conn.execute("INSERT INTO orders VALUES (?,?,?,?)", (oid, cust, f"{year}-{month:02d}-{rng.randint(1, 28):02d}", status))
        conn.executemany("INSERT INTO order_items VALUES (?,?,?)", [(oid, p, q) for p, q in items])
    conn.commit()
    conn.close()


TASK = """The company database is at ~/shop.db (SQLite). Please:
1. Find the top 3 customers by paid revenue in 2026 (revenue = quantity x price, orders with status 'paid' only) and save
   them to ~/top_customers.csv with the header `customer,revenue`, highest first, revenue rounded to 2 decimals.
2. Tell me which product category has the highest share of refunded orders (an order counts for a category if it
   contains at least one product from it), and what that share is as a percentage.
3. Tell me how many customers placed no paid orders in 2026, and who they are."""


def main() -> None:
    with tempfile.TemporaryDirectory() as tmp:
        db = str(Path(tmp) / "shop.db")
        build_database(db)

        # ground truth, straight from the database
        conn = sqlite3.connect(db)
        top = conn.execute("""SELECT c.name, ROUND(SUM(i.quantity * p.price), 2) rev FROM orders o JOIN customers c ON c.id = o.customer_id
                              JOIN order_items i ON i.order_id = o.id JOIN products p ON p.id = i.product_id
                              WHERE o.status = 'paid' AND o.ordered_at >= '2026-01-01' GROUP BY c.id ORDER BY rev DESC LIMIT 3""").fetchall()
        cat, share = conn.execute("""SELECT category, 100.0 * SUM(status = 'refunded') / COUNT(*) share FROM
                                     (SELECT DISTINCT o.id, o.status, p.category FROM orders o JOIN order_items i ON i.order_id = o.id
                                      JOIN products p ON p.id = i.product_id) GROUP BY category ORDER BY share DESC LIMIT 1""").fetchone()
        idle = [r[0] for r in conn.execute("""SELECT name FROM customers WHERE id NOT IN
                    (SELECT customer_id FROM orders WHERE status = 'paid' AND ordered_at >= '2026-01-01')""")]
        before = conn.execute("SELECT COUNT(*) FROM orders").fetchone()[0]
        conn.close()

        shell = Shell()
        SqliteCommand(shell, {"~/shop.db".replace("~", "/home/agent"): db})
        result = run_agent(shell, TASK)

        report_csv = shell.fs.read_text("/home/agent/top_customers.csv") if shell.fs.is_file("/home/agent/top_customers.csv") else ""
        rows = [r for r in csv.reader(io.StringIO(report_csv)) if r]
        conn = sqlite3.connect(db)
        after = conn.execute("SELECT COUNT(*) FROM orders").fetchone()[0]
        conn.close()

        def close(a: str, b: float) -> bool:
            try:
                return abs(float(a) - b) < 0.011
            except ValueError:
                return False

        checks = [
            ("CSV has the header customer,revenue", bool(rows) and [c.strip().lower() for c in rows[0]] == ["customer", "revenue"]),
            ("CSV lists the right top 3 customers in order", [r[0] for r in rows[1:4]] == [n for n, _ in top]),
            ("CSV revenues are correct", len(rows) == 4 and all(close(r[1], v) for r, (_, v) in zip(rows[1:], top))),
            (f"answer names the category ({cat})", cat in result.answer.lower()),
            (f"answer gives the refund share (~{share:.1f}%)", any(abs(float(n) - share) < 0.1 for n in re.findall(r"\d+\.\d+", result.answer))),
            (f"answer names the idle customers ({', '.join(idle)})", all(n in result.answer for n in idle)),
            ("database was not modified", before == after),
        ]
        ok = report("SQL analyst", checks, result)
        print(f"(ground truth: top={top}, category={cat} {share:.2f}%, idle={idle})")
        raise SystemExit(0 if ok else 1)


if __name__ == "__main__":
    main()
