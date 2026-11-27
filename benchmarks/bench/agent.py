"""The agent loop on OpenRouter's chat completions API, the same for every arm, plus the spend ledger."""
import json
import threading
import time
import urllib.error
import urllib.request

SYSTEM = ("You are an autonomous agent working on a computer through the tools you are given. Use the tools to inspect and act; "
          "do not ask the user questions. When you are finished, reply with a short final answer and end it with a line of the form "
          "`ANSWER: <answer>`.")
MAX_TURNS = 25
MAX_TOKENS = 12000          # round 1, as pre-registered
MAX_TOKENS_ROUND2 = 32000   # round 2: the board saw replies cut at 12,000 in arm B (README, "Round 2")
MAX_EPISODE_TOKENS = 400_000  # a cost fuse: prompt + completion tokens over the episode; round 1's largest was 373k
URL = "https://openrouter.ai/api/v1/chat/completions"


class BudgetStop(Exception):
    pass


class KeyLimit(BudgetStop):
    """OpenRouter refused the request because the key's own spending limit is used up: stop like the budget cap."""


class Ledger:
    """Every request's cost goes to spend.jsonl as it happens, so the total survives an interruption."""

    def __init__(self, path, cap, reserve=1.00):  # 4 requests in flight, each at most a few tens of cents
        self.path, self.cap, self.reserve, self.lock = path, cap, reserve, threading.Lock()
        self.spent = 0.0
        if path.exists():
            for line in path.read_text().splitlines():
                if line.strip():
                    self.spent += json.loads(line)["cost"]

    def check(self):
        with self.lock:
            if self.spent + self.reserve > self.cap:
                raise BudgetStop(f"spent ${self.spent:.2f} of the ${self.cap:.2f} cap")

    def add(self, episode, cost, usage):
        with self.lock:
            self.spent += cost
            with self.path.open("a") as f:
                f.write(json.dumps({"t": round(time.time(), 1), "episode": episode, "cost": cost,
                                    "prompt_tokens": usage.get("prompt_tokens", 0), "completion_tokens": usage.get("completion_tokens", 0)}) + "\n")


def _post(body, key):
    data = json.dumps(body).encode()
    for attempt in range(6):
        req = urllib.request.Request(URL, data=data, headers={"Authorization": f"Bearer {key}", "Content-Type": "application/json",
                                                              "X-Title": "topwater-benchmark"})
        try:
            with urllib.request.urlopen(req, timeout=300) as r:
                out = json.loads(r.read())
            if "error" not in out and out.get("choices"):
                return out
            err = str(out.get("error"))[:300]
        except urllib.error.HTTPError as e:
            err = f"HTTP {e.code} {e.read()[:300]!r}"
            if e.code == 403 and "Key limit exceeded" in err:
                raise KeyLimit("the OpenRouter key's spending limit is reached")
            if e.code not in (408, 429, 500, 502, 503, 504):
                raise RuntimeError(err)
        except (urllib.error.URLError, TimeoutError, json.JSONDecodeError, ConnectionError) as e:
            err = f"{type(e).__name__}: {e}"
        time.sleep(5 * 2 ** attempt)
    raise RuntimeError(f"model call failed after retries: {err}")


def _cacheable(messages, model):
    """Anthropic models cache only where asked: mark the system prompt and the latest message. Others cache on their own."""
    if not model.startswith("anthropic/"):
        return messages
    out = [dict(m) for m in messages]
    for i in (0, len(out) - 1):
        m = out[i]
        if isinstance(m.get("content"), str) and m["content"] and m["role"] in ("system", "user", "tool"):
            m["content"] = [{"type": "text", "text": m["content"], "cache_control": {"type": "ephemeral"}}]
    return out


def run_episode(env, prompt, model, key, ledger, episode_id, seed, max_tokens=MAX_TOKENS):
    """Returns a dict: answer, stop, usage totals, tool calls, turns, transcript."""
    messages = [{"role": "system", "content": SYSTEM}, {"role": "user", "content": prompt}]
    totals = {"prompt_tokens": 0, "completion_tokens": 0, "cached_tokens": 0, "reasoning_tokens": 0, "cost": 0.0}
    calls, outputs, finish = 0, [], []
    for turn in range(1, MAX_TURNS + 1):
        ledger.check()
        body = {"model": model, "messages": _cacheable(messages, model), "tools": env.tools, "temperature": 0, "seed": seed,
                "max_tokens": max_tokens, "usage": {"include": True}}
        resp = _post(body, key)
        u = resp.get("usage") or {}
        cost = float(u.get("cost") or 0.0)
        ledger.add(episode_id, cost, u)
        totals["prompt_tokens"] += u.get("prompt_tokens", 0)
        totals["completion_tokens"] += u.get("completion_tokens", 0)
        totals["cached_tokens"] += (u.get("prompt_tokens_details") or {}).get("cached_tokens", 0) or 0
        totals["reasoning_tokens"] += (u.get("completion_tokens_details") or {}).get("reasoning_tokens", 0) or 0
        totals["cost"] += cost
        finish.append(resp["choices"][0].get("finish_reason"))
        msg = resp["choices"][0].get("message") or {"role": "assistant", "content": ""}
        msg.setdefault("role", "assistant")
        if msg.get("content") is None and not msg.get("tool_calls"):
            msg["content"] = ""
        messages.append(msg)  # echoed back as it came, reasoning fields included
        tcs = msg.get("tool_calls") or []
        if tcs and totals["prompt_tokens"] + totals["completion_tokens"] > MAX_EPISODE_TOKENS:
            return {"answer": "", "stop": "token_limit", "turns": turn, "tool_calls": calls, "outputs": outputs, "messages": messages,
                    "finish_reasons": finish, **totals}
        if not tcs:
            # a reply cut by the output limit is its own kind of stop, not an answer
            return {"answer": (msg.get("content") or "").strip(), "stop": "length" if finish[-1] == "length" else "answer", "turns": turn,
                    "tool_calls": calls, "outputs": outputs, "messages": messages, "finish_reasons": finish, **totals}
        for tc in tcs:
            calls += 1
            fn = tc.get("function") or {}
            try:
                args = json.loads(fn.get("arguments") or "{}")
                if not isinstance(args, dict):
                    raise ValueError
            except ValueError:
                out = "error: the tool arguments were not a JSON object"
            else:
                out = env.call(fn.get("name", ""), args)
            outputs.append(out)
            messages.append({"role": "tool", "tool_call_id": tc.get("id", ""), "content": out})
    return {"answer": "", "stop": "turn_limit", "turns": MAX_TURNS, "tool_calls": calls, "outputs": outputs, "messages": messages, "finish_reasons": finish, **totals}
