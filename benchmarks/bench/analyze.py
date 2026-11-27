"""Turns episodes.jsonl into the tables in RESULTS.md and a summary.json. Standard library only.

Success rates get Wilson 95% intervals. Arm differences are paired: McNemar's exact test on episodes matched by
(model, task, repeat), and a bootstrap over tasks (10,000 resamples, seed 0) for the interval on the difference,
since tasks, not episodes, are the independent unit.
"""
import collections
import json
import math
import random
from pathlib import Path

ARMS = {"A": "Topwater bash", "B": "Classic tools", "C": "Docker bash"}
MARGIN = 0.05  # non-inferiority margin from the README


def wilson(k, n, z=1.96):
    if n == 0:
        return (float("nan"),) * 3
    p = k / n
    d = 1 + z * z / n
    c = (p + z * z / (2 * n)) / d
    h = z * math.sqrt(p * (1 - p) / n + z * z / (4 * n * n)) / d
    return p, c - h, c + h


def mcnemar(b, c):
    """Exact two-sided p for b vs c discordant pairs."""
    n = b + c
    if n == 0:
        return 1.0
    k = min(b, c)
    p = sum(math.comb(n, i) for i in range(k + 1)) / 2 ** n
    return min(1.0, 2 * p)


def pct(x):
    return "n/a" if x != x else f"{100 * x:.1f}%"


def load(out: Path):
    eps = {}
    for line in (out / "episodes.jsonl").read_text().splitlines():
        if line.strip():
            e = json.loads(line)
            if e["status"] == "done":
                eps[e["key"]] = e  # a rerun replaces an earlier record
    return list(eps.values())


def paired(eps, x, y, rng_seed=0):
    by = {(e["model"], e["task"], e["rep"], e["arm"]): e["success"] for e in eps}
    pairs = [(k[0], k[1], k[2], by[k], by[(k[0], k[1], k[2], y)]) for k in by if k[3] == x and (k[0], k[1], k[2], y) in by]
    if not pairs:
        return None
    b = sum(1 for p in pairs if p[3] and not p[4])
    c = sum(1 for p in pairs if not p[3] and p[4])
    diff = sum(p[3] - p[4] for p in pairs) / len(pairs)
    per_task = collections.defaultdict(list)
    for p in pairs:
        per_task[p[1]].append(p[3] - p[4])
    tasks = sorted(per_task)
    rng = random.Random(rng_seed)
    boots = []
    for _ in range(10000):
        sample = [per_task[rng.choice(tasks)] for _ in tasks]
        flat = [v for s in sample for v in s]
        boots.append(sum(flat) / len(flat))
    boots.sort()
    return {"pairs": len(pairs), "x_only": b, "y_only": c, "diff": diff, "ci": (boots[249], boots[9749]), "p_mcnemar": mcnemar(b, c)}


def analyze(out: Path):
    eps = load(out)
    main = [e for e in eps if e["category"] != "safety"]
    safety = [e for e in eps if e["category"] == "safety"]
    models = sorted({e["model"] for e in eps})
    arms = [a for a in ARMS if any(e["arm"] == a for e in eps)]
    lines, summary = [], {"episodes": len(eps), "main_episodes": len(main), "safety_episodes": len(safety)}

    def table(header, rows):
        lines.append("| " + " | ".join(header) + " |")
        lines.append("|" + "---|" * len(header))
        for r in rows:
            lines.append("| " + " | ".join(str(x) for x in r) + " |")
        lines.append("")

    # success per arm and model
    lines.append("### Success rate (capability tasks), Wilson 95% CI\n")
    rows, summary["success"] = [], {}
    for a in arms:
        row = [ARMS[a]]
        for m in models + ["all"]:
            sel = [e for e in main if e["arm"] == a and (m == "all" or e["model"] == m)]
            k, n = sum(e["success"] for e in sel), len(sel)
            p, lo, hi = wilson(k, n)
            summary["success"][f"{a}/{m}"] = {"k": k, "n": n, "p": p, "lo": lo, "hi": hi}
            row.append(f"{pct(p)} [{pct(lo)}, {pct(hi)}] ({k}/{n})")
        rows.append(row)
    table(["arm"] + models + ["all models"], rows)

    # paired comparisons
    lines.append("### Paired differences (same model, task, repeat)\n")
    rows, summary["paired"] = [], {}
    for x, y in (("A", "B"), ("A", "C"), ("C", "B")):
        for subset, sel in (("all tasks", main), ("without long-tail", [e for e in main if e["category"] != "longtail"])):
            for m in models + ["all"]:
                r = paired([e for e in sel if m == "all" or e["model"] == m], x, y)
                if r is None:
                    continue
                summary["paired"][f"{x}-{y}/{subset}/{m}"] = r
                if m == "all" or subset == "all tasks":
                    rows.append([f"{ARMS[x]} − {ARMS[y]}", subset, m, r["pairs"], f"{100 * r['diff']:+.1f} pts",
                                 f"[{100 * r['ci'][0]:+.1f}, {100 * r['ci'][1]:+.1f}]", f"{r['x_only']} / {r['y_only']}", f"{r['p_mcnemar']:.3f}"])
    table(["comparison", "tasks", "model", "pairs", "difference", "95% CI (task bootstrap)", f"only first / only second solved", "McNemar p"], rows)

    # per category
    lines.append("### Success by task category (all models)\n")
    cats = sorted({e["category"] for e in main})
    rows = []
    for c in cats:
        row = [c, len({e["task"] for e in main if e["category"] == c})]
        for a in arms:
            sel = [e for e in main if e["arm"] == a and e["category"] == c]
            row.append(f"{pct(sum(e['success'] for e in sel) / len(sel)) if sel else 'n/a'} ({sum(e['success'] for e in sel)}/{len(sel)})")
        rows.append(row)
    table(["category", "tasks"] + [ARMS[a] for a in arms], rows)

    # tokens and cost
    lines.append("### Tokens, cost and effort per episode (capability tasks)\n")
    rows, summary["cost"] = [], {}
    for a in arms:
        for m in models + ["all"]:
            sel = [e for e in main if e["arm"] == a and (m == "all" or e["model"] == m)]
            if not sel:
                continue
            n, wins = len(sel), sum(e["success"] for e in sel)
            cost = sum(e["cost"] for e in sel)
            d = {"prompt": sum(e["prompt_tokens"] for e in sel) / n, "completion": sum(e["completion_tokens"] for e in sel) / n,
                 "cached_share": sum(e["cached_tokens"] for e in sel) / max(1, sum(e["prompt_tokens"] for e in sel)),
                 "cost": cost / n, "cost_per_success": cost / wins if wins else float("nan"),
                 "turns": sum(e["turns"] for e in sel) / n, "tool_calls": sum(e["tool_calls"] for e in sel) / n,
                 "wall_s": sum(e["wall_s"] for e in sel) / n,
                 "tokens_per_success": sum(e["prompt_tokens"] + e["completion_tokens"] for e in sel) / wins if wins else float("nan")}
            summary["cost"][f"{a}/{m}"] = d
            rows.append([ARMS[a], m, f"{d['prompt']:,.0f}", f"{d['completion']:,.0f}", f"{100 * d['cached_share']:.0f}%", f"{d['tokens_per_success']:,.0f}",
                         f"${d['cost']:.4f}", f"${d['cost_per_success']:.4f}", f"{d['turns']:.1f}", f"{d['tool_calls']:.1f}", f"{d['wall_s']:.0f}"])
    table(["arm", "model", "prompt tok", "completion tok", "cached", "tokens / success", "cost", "cost / success", "turns", "tool calls", "wall s"], rows)

    # failures
    lines.append("### Failures by kind (capability tasks)\n")
    rows = []
    for a in arms:
        fails = [e for e in main if e["arm"] == a and not e["success"]]
        kinds = collections.Counter("turn limit" if e["stop"] == "turn_limit" else
                                    "no file / wrong end state" if any(w in e["reason"] for w in ("file rows", "not JSON", "got []", "subject=", "refunds", "purchase orders", "listing", "left=", "rows=None")) and e["stop"] == "answer" else
                                    "wrong or missing answer" for e in fails)
        restarts = sum(e.get("restarts", 0) for e in main if e["arm"] == a)
        friction = sum(e.get("not_found", 0) for e in main if e["arm"] == a)
        errs = sum(e.get("tool_errors", 0) for e in main if e["arm"] == a)
        rows.append([ARMS[a], len(fails), ", ".join(f"{k}: {v}" for k, v in kinds.most_common()), errs, friction, restarts])
    table(["arm", "failures", "kinds", "tool calls that errored", "unsupported command/option", "shell restarts"], rows)

    lines.append("### Per task: solved / episodes\n")
    rows = []
    for t in sorted({e["task"] for e in main}, key=lambda t: (next(e["category"] for e in main if e["task"] == t), t)):
        row = [t]
        for a in arms:
            sel = [e for e in main if e["arm"] == a and e["task"] == t]
            row.append(f"{sum(e['success'] for e in sel)}/{len(sel)}")
        rows.append(row)
    table(["task"] + [ARMS[a] for a in arms], rows)

    # H3
    lines.append("### Safety (H3): episodes where something outside the sandbox was reached or leaked\n")
    rows, summary["safety"] = [], {}
    for a in arms:
        sel = [e for e in safety if e["arm"] == a]
        every = [e for e in eps if e["arm"] == a]
        b = [e for e in every if e.get("breaches")]
        attempted = sum(1 for e in sel if e["tool_calls"] > 0)
        summary["safety"][a] = {"safety_episodes": len(sel), "attempted": attempted, "episodes_scanned": len(every),
                                "breaches": [(e["key"], e["breaches"]) for e in b]}
        p, lo, hi = wilson(len([e for e in sel if e.get("breaches")]), len(sel))
        rows.append([ARMS[a], len(sel), attempted, len([e for e in sel if e.get("breaches")]), f"[{pct(lo)}, {pct(hi)}]", len(every), len(b),
                     "; ".join(sorted({x for e in b for x in e["breaches"]})) or "none"])
    table(["arm", "safety episodes", "used a tool (attempted)", "breaches", "95% CI", "all episodes scanned", "episodes with a breach", "kinds"], rows)

    lines.append("Per safety task, episodes that attempted (used a tool) / total:\n")
    rows = []
    for t in sorted({e["task"] for e in safety}):
        rows.append([t] + [f"{sum(1 for e in safety if e['arm'] == a and e['task'] == t and e['tool_calls'] > 0)}/{sum(1 for e in safety if e['arm'] == a and e['task'] == t)}" for a in arms])
    table(["task"] + [ARMS[a] for a in arms], rows)

    summary["total_cost"] = sum(e["cost"] for e in eps)
    (out / "summary.json").write_text(json.dumps(summary, indent=1, default=str) + "\n")
    (out / "tables.md").write_text("\n".join(lines))
    print("\n".join(lines))
    print(f"episodes: {len(eps)} ({len(main)} capability, {len(safety)} safety); cost of these episodes ${summary['total_cost']:.2f}")
