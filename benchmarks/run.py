"""Topwater benchmark: one bash tool on Topwater vs classic function calling vs real bash in Docker.

    python3 benchmarks/run.py selftest                      # no model calls: checkers, reference solutions on arms A and C
    python3 benchmarks/run.py run --budget 23               # the full design (resumable: finished episodes are skipped)
    python3 benchmarks/run.py run --tasks q_open_count,f_port_config --reps 1 --out benchmarks/results/pilot
    python3 benchmarks/run.py analyze                       # tables and summary.json from the episodes
    Round 2 (README, "Round 2"): --tasks round2 --out benchmarks/results/round2; the exact commands are in the README.

The OpenRouter key comes from $OPENROUTER_API_KEY or ~/.config/topwater/openrouter.env. It is never written anywhere.
See benchmarks/README.md for the method.
"""
import argparse
import json
import os
import sys
import threading
import time
import traceback
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from bench import agent  # noqa: E402
from bench.arms import make_arm  # noqa: E402
from bench.shop import Shop  # noqa: E402
from bench.tasks import Ctx  # noqa: E402
from bench.tasks2 import ALL, BY_ID, ROUND1, ROUND2  # noqa: E402

MODELS = {"sonnet": "anthropic/claude-sonnet-5.5", "terra": "openai/gpt-5.6-terra", "gemini": "google/gemini-4-argon"}
HERE = Path(__file__).resolve().parent


def load_key():
    if os.environ.get("OPENROUTER_API_KEY"):
        return os.environ["OPENROUTER_API_KEY"]
    for line in (Path.home() / ".config/topwater/openrouter.env").read_text().splitlines():
        if line.startswith("OPENROUTER_API_KEY="):
            return line.split("=", 1)[1].strip().strip("'\"")
    raise SystemExit("no OPENROUTER_API_KEY")


def selftest(only=None):
    from bench.tasks import nums  # noqa: F401
    bad, gaps = 0, []
    for t in ALL:
        if t.category == "safety" or (only and t.id not in only):
            continue
        if t.expected:
            good = t.check(Ctx(f"Done.\nANSWER: {t.expected}", lambda p: None, lambda c: "", Shop()))[0]
            wrong = t.check(Ctx("ANSWER: I could not find it", lambda p: None, lambda c: "", Shop()))[0]
            if not good or wrong:
                print(f"FAIL answer check {t.id}: expected passes={good}, wrong passes={wrong}")
                bad += 1
        if t.reference:
            for arm in [a for a in t.arms.split(",") if a in ("A", "C", "Cp")]:
                for solve in (False, True):
                    shop = Shop()
                    env = make_arm(arm, shop, "selftest", t)
                    try:
                        out = env.call("bash", {"command": t.reference}) if solve else ""
                        # round 2's answer tasks also have references, so the untouched machine gets a wrong answer
                        answer = next((line for line in out.splitlines() if line.startswith("ANSWER:")),
                                      "ANSWER: I could not find it" if t.round == 2 else "ANSWER: " + t.expected)
                        passed, why = t.check(Ctx(answer, env.read, env.run, shop))
                    finally:
                        env.close()
                    if solve and arm == "A" and t.a_gap:  # expected to fail on Topwater: a finding, kept and reported
                        gaps.append(f"{t.id}: {t.a_gap}")
                        if passed:
                            print(f"FAIL {t.id}: the reference now passes on Topwater; clear a_gap ({t.a_gap})")
                            bad += 1
                        shop = Shop()  # the task must still be solvable on Topwater another way
                        env = make_arm("A", shop, "selftest", t)
                        out = env.call("bash", {"command": t.a_reference})
                        answer = next((line for line in out.splitlines() if line.startswith("ANSWER:")), "ANSWER: I could not find it")
                        if not t.check(Ctx(answer, env.read, env.run, shop))[0]:
                            print(f"FAIL {t.id}: the Topwater route (a_reference) does not pass\n  output: {out[:600]}")
                            bad += 1
                        continue
                    if passed != solve:
                        print(f"FAIL reference {t.id} arm {arm} solved={solve}: {why}\n  output: {out[:600]}")
                        bad += 1
        if not t.expected and not t.reference:
            print(f"FAIL {t.id} has neither an expected answer nor a reference solution")
            bad += 1
    # arm B can do the end-state work with its own tools
    shop = Shop()
    b = make_arm("B", shop, "selftest")
    assert "Engineering" in b.call("read_document", {"path": "~/docs/budget-2026.xlsx"})
    assert "Error" in b.call("list_shipments", {}) and "S-5001" in b.call("list_shipments", {})
    assert "users.json" in b.call("list_files", {"path": "~", "recursive": True})
    b.call("write_file", {"path": "~/out/inactive_admins.txt", "content": "\n".join(__import__("bench.tasks", fromlist=["x"]).INACTIVE_ADMINS) + "\n"})
    assert BY_ID["f_inactive_admins"].check(Ctx("", b.read, b.run, shop))[0]
    assert "Raise default timeout" in b.call("git", {"args": ["log", "--format=%an %s"], "cwd": "~/repo"})
    # round 2: arm B pages a large document; arm Bc's tools all reach its container
    b = make_arm("B", Shop(), "selftest", BY_ID["x_orders_xlsx"])
    page = b.call("read_document", {"path": "~/big/orders-2026.xlsx", "offset": 30001, "limit": 3})
    assert page.count("ORD-") == 3 and "ORD-0299" in page, page
    bc = make_arm("Bc", Shop(), "selftest", BY_ID["x_orders_xlsx"])
    try:
        assert bc.call("read_document", {"path": "~/big/orders-2026.xlsx", "offset": 30001, "limit": 3}) == page
        assert "orders-2026.xlsx" in bc.call("list_files", {"path": "~/big", "recursive": True})
        assert "wrote 6 bytes" in bc.call("write_file", {"path": "~/out/x.txt", "content": "hello\n"})
        assert bc.call("read_file", {"path": "~/out/x.txt"}) == "hello\n" and "binary" in bc.call("read_file", {"path": "~/big/orders-2026.xlsx"})
        assert bc.call("run_python", {"code": "import zipfile; print(len(zipfile.ZipFile('big/orders-2026.xlsx').namelist()), open('out/x.txt').read())"}).startswith("5 hello")
        assert "Raise default timeout" in bc.call("git", {"args": ["log", "--format=%an %s"], "cwd": "~/repo"})
        assert "Error" in bc.call("list_shipments", {}) and "S-5001" in bc.call("list_shipments", {})  # fails once by design (recovery)
    finally:
        bc.close()
    for g in gaps:
        print(f"finding (reference fails on Topwater, as recorded): {g}")
    print(f"selftest: {len([t for t in ALL if t.category != 'safety'])} tasks + {len([t for t in ALL if t.category == 'safety'])} safety; "
          f"{'all checks pass' if not bad else f'{bad} failures'}")
    return 1 if bad else 0


def run(args):
    from bench.safety import Canaries
    key = load_key()
    out = Path(args.out)
    (out / "transcripts").mkdir(parents=True, exist_ok=True)
    episodes_path = out / "episodes.jsonl"
    done = {}
    if episodes_path.exists():
        for line in episodes_path.read_text().splitlines():
            if line.strip():
                e = json.loads(line)
                if e["status"] == "done":
                    done[e["key"]] = e
    ledger = agent.Ledger(HERE / "results" / "spend.jsonl", args.budget)  # one ledger for every run: the cap is total
    canaries = Canaries(key)
    models = [m.strip() for m in args.models.split(",")]
    arms = [a.strip() for a in args.arms.split(",")]
    # "all" is round 1's 43 tasks, as pre-registered (the same list, so the seeded order below is unchanged)
    tasks = {"all": ROUND1, "round1": ROUND1, "round2": ROUND2}.get(args.tasks) or [BY_ID[t.strip()] for t in args.tasks.split(",")]
    import random  # each repeat takes the tasks in its own seeded order, so a repeat cut short by the budget still spans the categories
    jobs = [(rep, t, m, a) for rep in range(1, args.reps + 1) for t in random.Random(rep).sample(tasks, len(tasks)) for m in models for a in arms
            if a in t.arms.split(",")]
    todo = [j for j in jobs if f"{j[2]}|{j[3]}|{j[1].id}|r{j[0]}" not in done]
    print(f"{len(jobs)} episodes in the design, {len(jobs) - len(todo)} already done, {len(todo)} to run; "
          f"spent so far ${ledger.spent:.2f} of ${args.budget:.2f}", flush=True)
    write_lock, stop = threading.Lock(), threading.Event()

    def episode(job):
        rep, task, m, a = job
        if stop.is_set():
            return
        k = f"{m}|{a}|{task.id}|r{rep}"
        prompt = canaries.prompt(task.prompt, k) if task.category == "safety" else task.prompt
        shop = Shop()
        free_before = canaries.free() if task.id == "s_fill_disk" else None
        rec = {"key": k, "model": m, "arm": a, "task": task.id, "category": task.category, "rep": rep}
        t0 = time.time()
        env = None
        try:
            env = make_arm(a, shop, k.replace("|", "-").replace("_", "-").lower(), task)
            r = agent.run_episode(env, prompt, MODELS[m], key, ledger, k, seed=1000 + rep,
                                  max_tokens=agent.MAX_TOKENS if task.round == 1 else agent.MAX_TOKENS_ROUND2)
            passed, why = task.check(Ctx(r["answer"], env.read, env.run, shop)) if r["stop"] == "answer" else (False, r["stop"].replace("_", " "))
            rec["round"] = task.round
            breaches = canaries.scan(k, r["outputs"] + [r["answer"]], free_before)
            rec.update(status="done", success=bool(passed), reason=why, answer=canaries.redact(r["answer"])[-2000:], stop=r["stop"],
                       turns=r["turns"], tool_calls=r["tool_calls"], prompt_tokens=r["prompt_tokens"], completion_tokens=r["completion_tokens"],
                       cached_tokens=r["cached_tokens"], reasoning_tokens=r["reasoning_tokens"], cost=round(r["cost"], 6),
                       restarts=getattr(env, "restarts", 0), breaches=breaches, finish_reasons=r["finish_reasons"],
                       tool_errors=sum(1 for o in r["outputs"] if "[exit code" in o or o.startswith("Error")),
                       not_found=sum(1 for o in r["outputs"] if "command not found" in o or "unknown option" in o or "unknown switch" in o or "not supported" in o))
            (out / "transcripts" / f"{k.replace('|', '__')}.json").write_text(canaries.redact(json.dumps(
                {"key": k, "prompt": prompt, "tools": env.tools, "messages": r["messages"]}, indent=1)))
        except agent.BudgetStop as e:
            stop.set()
            print(f"budget stop: {e}", flush=True)
            return
        except Exception as e:
            rec.update(status="error", error=canaries.redact(f"{type(e).__name__}: {e}")[:500])
            traceback.print_exc()
        finally:
            if env is not None:
                try:
                    env.close()
                except Exception:
                    traceback.print_exc()
        rec["wall_s"] = round(time.time() - t0, 1)
        with write_lock:
            with episodes_path.open("a") as f:
                f.write(json.dumps(rec) + "\n")
        flag = "PASS" if rec.get("success") else ("ERR " if rec["status"] != "done" else "fail")
        print(f"[${ledger.spent:6.2f}] {flag} {k:42} turns={rec.get('turns', '-')} calls={rec.get('tool_calls', '-')} "
              f"cost={rec.get('cost', 0):.4f} {rec.get('reason', rec.get('error', ''))[:90]}", flush=True)

    with ThreadPoolExecutor(max_workers=min(4, args.workers)) as pool:  # never more than 4 requests at once
        list(pool.map(episode, todo))
    canaries.close()
    print(f"spent ${ledger.spent:.2f}", flush=True)


def main():
    ap = argparse.ArgumentParser()
    sub = ap.add_subparsers(dest="cmd", required=True)
    sub.add_parser("selftest").add_argument("only", nargs="*", help="task ids (default: all)")
    r = sub.add_parser("run")
    r.add_argument("--models", default="sonnet,terra,gemini")
    r.add_argument("--arms", default="A,B,C", help="A, B, C, and for round 2 Bc and Cp; each task runs only in the arms it lists")
    r.add_argument("--tasks", default="all", help="all (= round1), round1, round2, or task ids separated by commas")
    r.add_argument("--reps", type=int, default=3)
    r.add_argument("--budget", type=float, default=23.0)
    r.add_argument("--workers", type=int, default=4)
    r.add_argument("--out", default=str(HERE / "results" / "main"))
    an = sub.add_parser("analyze")
    an.add_argument("--out", default=str(HERE / "results" / "main"))
    a = ap.parse_args()
    if a.cmd == "selftest":
        sys.exit(selftest(set(a.only)))
    if a.cmd == "run":
        run(a)
    if a.cmd == "analyze":
        from bench.analyze import analyze
        analyze(Path(a.out))


if __name__ == "__main__":
    main()
