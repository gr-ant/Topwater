"""A small jq: paths (.a.b, .[0], .[], ."key"), pipes, select(), map(), has(), [ ... ], comparisons,
and/or, keys/length/type/not/add.

shortcut: no object construction, variables or string interpolation; add them if agents need them.
"""
from __future__ import annotations

import json
import re

from ..vfs import ShellError
from . import Args, inputs, split_lines

_NOTHING = object()


def _error(msg: str) -> ShellError:
    return ShellError("error: " + msg, 5)


def _type(v) -> str:
    if v is None:
        return "null"
    if isinstance(v, bool):
        return "boolean"
    if isinstance(v, (int, float)):
        return "number"
    if isinstance(v, str):
        return "string"
    return "array" if isinstance(v, list) else "object"


def _truthy(v) -> bool:
    return v is not None and v is not False


def _split_top(s: str, sep: str) -> list[str]:
    """Splits on sep outside brackets and strings."""
    parts, depth, start, i = [], 0, 0, 0
    while i < len(s):
        c = s[i]
        if c == '"':
            i += 1
            while i < len(s) and s[i] != '"':
                i += 2 if s[i] == "\\" else 1
        elif c in "([{":
            depth += 1
        elif c in ")]}":
            depth -= 1
        elif depth == 0 and s.startswith(sep, i):
            parts.append(s[start:i])
            i += len(sep)
            start = i
            continue
        i += 1
    parts.append(s[start:])
    return parts


def _compare(l, r, op: str) -> bool:
    if _type(l) == _type(r) and _type(l) in ("number", "string"):
        cmp = (l > r) - (l < r)
    elif json.dumps(l) == json.dumps(r):
        cmp = 0
    else:
        order = ["null", "boolean", "number", "string", "array", "object"]
        cmp = (order.index(_type(l)) > order.index(_type(r))) - (order.index(_type(l)) < order.index(_type(r))) or 1
    return {"==": cmp == 0, "!=": cmp != 0, ">": cmp > 0, "<": cmp < 0, ">=": cmp >= 0, "<=": cmp <= 0}[op]


def evaluate(f: str, data) -> list:
    f = f.strip()
    pipe = _split_top(f, "|")
    if len(pipe) > 1:
        results = [data]
        for stage in pipe:
            results = [out for r in results for out in evaluate(stage, r)]
        return results
    for op in (" and ", " or "):
        p = _split_top(f, op)
        if len(p) > 1:
            left = any(_truthy(x) for x in evaluate(p[0], data))
            right = any(_truthy(x) for x in evaluate(op.join(p[1:]), data))
            return [left and right if op == " and " else left or right]
    for op in ("==", "!=", ">=", "<=", ">", "<"):
        p = _split_top(f, op)
        if len(p) == 2:
            return [_compare(l, r, op) for l in evaluate(p[0], data) for r in evaluate(p[1], data)]
    return _term(f, data)


def _term(f: str, data) -> list:
    if f.startswith("(") and f.endswith(")"):
        return evaluate(f[1:-1], data)
    if f.startswith("[") and f.endswith("]"):
        return [evaluate(f[1:-1], data)]
    if f.startswith("select(") and f.endswith(")"):
        return [data] if any(_truthy(x) for x in evaluate(f[7:-1], data)) else []
    if f.startswith("map(") and f.endswith(")"):
        if not isinstance(data, list):
            raise _error(f"Cannot iterate over {_type(data)}")
        return [[out for x in data for out in evaluate(f[4:-1], x)]]
    if f.startswith("has(") and f.endswith(")"):
        key = evaluate(f[4:-1], data)[0]
        if isinstance(data, dict):
            return [key in data]
        if isinstance(data, list):
            return [isinstance(key, int) and 0 <= key < len(data)]
        raise _error(f"Cannot check whether {_type(data)} has a key")
    if f == "":
        raise _error("empty filter")
    if f == "keys":
        if isinstance(data, dict):
            return [sorted(data)]
        if isinstance(data, list):
            return [list(range(len(data)))]
        raise _error(f"{_type(data)} has no keys")
    if f == "length":
        if data is None:
            return [0]
        if isinstance(data, (dict, list, str)):
            return [len(data)]
        if _type(data) == "number":
            return [abs(data)]
        raise _error(f"{_type(data)} has no length")
    if f == "type":
        return [_type(data)]
    if f == "not":
        return [not _truthy(data)]
    if f == "add":
        if not isinstance(data, list):
            raise _error(f"Cannot add {_type(data)}")
        if not data:
            return [None]
        types = {_type(x) for x in data}
        if types == {"number"}:
            return [sum(data)]
        if types == {"string"}:
            return ["".join(data)]
        if types == {"array"}:
            return [[y for x in data for y in x]]
        raise _error("cannot add mixed types")
    if f in ("true", "false", "null") or f.startswith('"') or re.fullmatch(r"-?\d+(\.\d+)?([eE][-+]?\d+)?", f):
        return [json.loads(f)]
    if f.startswith("."):
        return _path(f, data)
    raise _error(f"unsupported filter '{f}' (supported: paths, |, select, map, has, [..], keys, length, type, not, add, comparisons)")


def _field(v, key):
    if v is None:
        return None
    if isinstance(v, dict):
        return v.get(key)
    raise _error(f'Cannot index {_type(v)} with "{key}"')


def _index(v, idx):
    if v is None:
        return None
    if isinstance(v, list):
        k = idx + len(v) if idx < 0 else idx
        return v[k] if 0 <= k < len(v) else None
    raise _error(f"Cannot index {_type(v)} with number")


def _iterate(v):
    if isinstance(v, list):
        return list(v)
    if isinstance(v, dict):
        return list(v.values())
    raise _error(f"Cannot iterate over {_type(v)}")


def _path(f: str, data) -> list:
    cur = [data]
    i = 1
    while i < len(f):
        if f[i] == ".":
            i += 1
            continue
        if f[i] == "[":
            close = f.find("]", i)
            if close < 0:
                raise _error("unterminated [")
            inner = f[i + 1:close].strip()
            i = close + 1
            if not inner:
                step = _iterate
            elif inner.startswith('"'):
                step = (lambda key: lambda v: [_field(v, key)])(json.loads(inner))
            elif re.fullmatch(r"-?\d+", inner):
                step = (lambda idx: lambda v: [_index(v, idx)])(int(inner))
            else:
                raise _error(f"unsupported index [{inner}]")
        elif f[i] == '"':
            close = i + 1
            while close < len(f) and f[close] != '"':
                close += 2 if f[close] == "\\" else 1
            step = (lambda key: lambda v: [_field(v, key)])(json.loads(f[i:close + 1]))
            i = close + 1
        else:
            m = re.match(r"[A-Za-z_][A-Za-z0-9_]*", f[i:])
            if not m:
                raise _error(f"syntax error in '{f}'")
            step = (lambda key: lambda v: [_field(v, key)])(m.group(0))
            i += m.end()
        optional = i < len(f) and f[i] == "?"
        if optional:
            i += 1
        nxt = []
        for v in cur:
            try:
                nxt.extend(step(v))
            except ShellError:
                if not optional:
                    raise
        cur = nxt
    return cur


def _sort_keys(v):
    if isinstance(v, dict):
        return {k: _sort_keys(v[k]) for k in sorted(v)}
    if isinstance(v, list):
        return [_sort_keys(x) for x in v]
    return v


def _jq(ctx):
    a = Args.parse(ctx.args, "rcnsSj", longs="raw-output=r,compact-output=c,null-input=n,slurp=s,sort-keys=S,join-output=j")
    filt = a.positional[0] if a.positional else "."
    code = 0

    def fail():
        nonlocal code
        code = 2

    docs = []
    if a.has("n"):
        docs.append(None)
    else:
        for _, text in inputs(ctx, a.positional[1:], fail):
            if not text.strip():
                continue
            try:
                docs.append(json.loads(text))
            except json.JSONDecodeError:
                for line in split_lines(text):  # JSON lines
                    if line.strip():
                        try:
                            docs.append(json.loads(line))
                        except json.JSONDecodeError as e:
                            raise ShellError(f"error: cannot parse input: {e}", 2) from None
    if a.has("s"):
        docs = [docs]

    compact = a.has("c") or a.has("j")
    for doc in docs:
        for result in evaluate(filt, doc):
            if a.has("S"):
                result = _sort_keys(result)
            if (a.has("r") or a.has("j")) and isinstance(result, str):
                text = result
            elif compact:
                text = json.dumps(result, ensure_ascii=False, separators=(",", ":"))
            else:
                text = json.dumps(result, ensure_ascii=False, indent=2)
            ctx.stdout.write(text + ("" if a.has("j") else "\n"))
    return code


def register(shell) -> None:
    shell.register_command("jq", _jq, "Command-line JSON processor (subset)")
