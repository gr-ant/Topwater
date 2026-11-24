"""jq: a parser and evaluator for the jq language as agents use it.

Pipes, `,`, `//`, arithmetic, comparisons, and/or, object and array construction, string interpolation and @formats,
variables (`as $x` with destructuring, --arg, --argjson, $ENV, $ARGS), if/elif/else, try/catch, `?`, reduce, foreach,
path expressions with assignment (`=`, `|=`, `+=` ...) and del(), and the common builtins (select, map, sort_by,
group_by, to_entries, test/sub/gsub/capture, split/join, paths, tostring/tonumber, limit, first, ...).
Numbers are doubles, as in jq. Mirrored by Jq.cs.

shortcut: no `def`, `label`/`break`, `input`/`inputs`, or strftime-style date builtins (only now/todate/fromdate);
add them if agents need them.
"""
from __future__ import annotations

import base64
import calendar
import functools
import json
import math
import re
import time
from urllib.parse import quote

from ..vfs import ShellError
from . import inputs


class JqError(Exception):
    """A runtime error raised by the jq program (catchable with try)."""

    def __init__(self, value):
        super().__init__(value if isinstance(value, str) else dump(value))
        self.value = value


class _Compile(Exception):
    pass


# ---------- values: None, bool, float, str, list, dict ----------

def type_name(v) -> str:
    if v is None:
        return "null"
    if isinstance(v, bool):
        return "boolean"
    if isinstance(v, float):
        return "number"
    if isinstance(v, str):
        return "string"
    return "array" if isinstance(v, list) else "object"


def _truthy(v) -> bool:
    return v is not None and v is not False


def fmt_number(v: float) -> str:
    if math.isnan(v):
        return "null"
    if math.isinf(v):
        return "1.7976931348623157e+308" if v > 0 else "-1.7976931348623157e+308"
    if v.is_integer() and abs(v) < 1e17:
        return str(int(v))
    return repr(v)


def _quote(s: str, ascii_only: bool = False) -> str:
    out = ['"']
    for c in s:
        o = ord(c)
        if c == '"':
            out.append('\\"')
        elif c == "\\":
            out.append("\\\\")
        elif c == "\n":
            out.append("\\n")
        elif c == "\t":
            out.append("\\t")
        elif c == "\r":
            out.append("\\r")
        elif c == "\b":
            out.append("\\b")
        elif c == "\f":
            out.append("\\f")
        elif o < 0x20 or o == 0x7F:
            out.append(f"\\u{o:04x}")
        elif ascii_only and o > 0x7E:
            if o > 0xFFFF:
                o -= 0x10000
                out.append(f"\\u{0xD800 + (o >> 10):04x}\\u{0xDC00 + (o & 0x3FF):04x}")
            else:
                out.append(f"\\u{o:04x}")
        else:
            out.append(c)
    out.append('"')
    return "".join(out)


def dump(v, indent: str = "", sort_keys: bool = False, ascii_only: bool = False, level: int = 0) -> str:
    """Serialises like jq. indent is the per-level indentation ("" = compact)."""
    if v is None:
        return "null"
    if v is True:
        return "true"
    if v is False:
        return "false"
    if isinstance(v, float):
        return fmt_number(v)
    if isinstance(v, str):
        return _quote(v, ascii_only)
    if not v:
        return "[]" if isinstance(v, list) else "{}"
    if isinstance(v, list):
        items = [dump(x, indent, sort_keys, ascii_only, level + 1) for x in v]
    else:
        keys = sorted(v) if sort_keys else list(v)
        sep = ": " if indent else ":"
        items = [_quote(k, ascii_only) + sep + dump(v[k], indent, sort_keys, ascii_only, level + 1) for k in keys]
    open_, close = ("[", "]") if isinstance(v, list) else ("{", "}")
    if not indent:
        return open_ + ",".join(items) + close
    pad = indent * (level + 1)
    return open_ + "\n" + ",\n".join(pad + x for x in items) + "\n" + indent * level + close


def _rank(v) -> int:
    if v is None:
        return 0
    if v is False:
        return 1
    if v is True:
        return 2
    return {"number": 3, "string": 4, "array": 5, "object": 6}[type_name(v)]


def compare(a, b) -> int:
    """jq's total order: null < false < true < numbers < strings < arrays < objects."""
    ra, rb = _rank(a), _rank(b)
    if ra != rb:
        return -1 if ra < rb else 1
    if isinstance(a, (float, str)):
        return (a > b) - (a < b)
    if isinstance(a, list):
        for x, y in zip(a, b):
            c = compare(x, y)
            if c:
                return c
        return (len(a) > len(b)) - (len(a) < len(b))
    if isinstance(a, dict):
        c = compare(sorted(a), sorted(b))
        if c:
            return c
        for k in sorted(a):
            c = compare(a[k], b[k])
            if c:
                return c
    return 0


_sort_key = functools.cmp_to_key(compare)


def _floats(v):
    """Converts parsed JSON so that every number is a float (jq numbers are doubles)."""
    if isinstance(v, bool) or v is None or isinstance(v, str):
        return v
    if isinstance(v, (int, float)):
        return float(v)
    if isinstance(v, list):
        return [_floats(x) for x in v]
    return {k: _floats(x) for k, x in v.items()}


def parse_json_stream(text: str) -> list:
    """Parses whitespace-separated JSON values (jq accepts several documents in one input)."""
    dec = json.JSONDecoder()
    out, i, n = [], 0, len(text)
    while True:
        while i < n and text[i] in " \t\r\n":
            i += 1
        if i >= n:
            return out
        try:
            v, i = dec.raw_decode(text, i)
        except json.JSONDecodeError as e:
            line = text.count("\n", 0, e.pos) + 1
            raise ShellError(f"error (at <stdin>:{line}): Cannot parse input: {e.msg} at line {line}", 2) from None
        out.append(_floats(v))


# ---------- lexer ----------

_PUNCT = ["|=", "+=", "-=", "*=", "/=", "%=", "//=", "==", "!=", "<=", ">=", "//", "..",
          "|", ",", "=", "<", ">", "+", "-", "*", "/", "%", "(", ")", "[", "]", "{", "}", ":", ";", "?", "."]
_KEYWORDS = {"if", "then", "elif", "else", "end", "as", "reduce", "foreach", "try", "catch", "and", "or", "def", "label"}
_ESCAPES = {"n": "\n", "t": "\t", "r": "\r", "b": "\b", "f": "\f", '"': '"', "\\": "\\", "/": "/"}


def _lex(src: str) -> list[tuple]:
    toks: list[tuple] = []
    i, n = 0, len(src)
    while i < n:
        c = src[i]
        if c.isspace():
            i += 1
            continue
        if c == "#":
            while i < n and src[i] != "\n":
                i += 1
            continue
        if c == '"':
            parts, i = _lex_string(src, i + 1)
            toks.append(("str", parts))
            continue
        if c.isdigit() or (c == "." and i + 1 < n and src[i + 1].isdigit()):
            m = re.match(r"\d*\.?\d*([eE][+-]?\d+)?", src[i:])
            toks.append(("num", float(m.group(0))))
            i += m.end()
            continue
        if c == "." and i + 1 < n and (src[i + 1].isalpha() or src[i + 1] == "_"):
            m = re.match(r"[A-Za-z_][A-Za-z0-9_]*", src[i + 1:])
            toks.append(("field", m.group(0)))
            i += 1 + m.end()
            continue
        if c in "$@" or c.isalpha() or c == "_":
            m = re.match(r"[$@]?[A-Za-z_][A-Za-z0-9_]*(::[A-Za-z_][A-Za-z0-9_]*)*", src[i:])
            if not m:
                raise _Compile(f"syntax error, unexpected '{c}'")
            word = m.group(0)
            i += m.end()
            if word[0] == "$":
                toks.append(("var", word[1:]))
            elif word[0] == "@":
                toks.append(("format", word[1:]))
            elif word in _KEYWORDS:
                toks.append(("op", word))
            else:
                toks.append(("ident", word))
            continue
        for p in _PUNCT:
            if src.startswith(p, i):
                toks.append(("op", p))
                i += len(p)
                break
        else:
            raise _Compile(f"syntax error, unexpected '{c}'")
    toks.append(("eof", None))
    return toks


def _lex_string(src: str, i: int) -> tuple[list, int]:
    """A string literal body starting after the quote: returns (parts, index after the closing quote), where parts
    are plain strings and ("interp", source) pieces for \\(...)."""
    parts: list = []
    buf: list[str] = []
    n = len(src)
    while i < n and src[i] != '"':
        c = src[i]
        if c != "\\":
            buf.append(c)
            i += 1
            continue
        e = src[i + 1] if i + 1 < n else ""
        if e == "(":
            depth, j = 1, i + 2
            while j < n and depth:
                if src[j] == '"':
                    _, j = _lex_string(src, j + 1)
                    continue
                depth += {"(": 1, ")": -1}.get(src[j], 0)
                j += 1
            if depth:
                raise _Compile("unterminated string interpolation")
            if buf:
                parts.append("".join(buf))
                buf = []
            parts.append(("interp", src[i + 2:j - 1]))
            i = j
            continue
        if e == "u" and re.fullmatch(r"[0-9a-fA-F]{4}", src[i + 2:i + 6]):
            code = int(src[i + 2:i + 6], 16)
            i += 6
            if 0xD800 <= code < 0xDC00 and src.startswith("\\u", i) and re.fullmatch(r"[0-9a-fA-F]{4}", src[i + 2:i + 6]):
                code = 0x10000 + ((code - 0xD800) << 10) + (int(src[i + 2:i + 6], 16) - 0xDC00)
                i += 6
            buf.append(chr(code) if not 0xD800 <= code <= 0xDFFF else "\ufffd")
            continue
        if e not in _ESCAPES:
            raise _Compile(f"invalid escape \\{e} in string literal")
        buf.append(_ESCAPES[e])
        i += 2
    if i >= n:
        raise _Compile("unterminated string literal")
    if buf or not parts:
        parts.append("".join(buf))
    return parts, i + 1


# ---------- parser ----------

class _Parser:
    """Recursive descent over jq's precedence, lowest first: `|`, `,`, `//`, assignment, or, and, comparison,
    + -, * / %, unary minus, postfix (.a [i] [] ?)."""

    def __init__(self, src: str):
        self.t = _lex(src)
        self.p = 0

    def peek(self, k: int = 0):
        return self.t[min(self.p + k, len(self.t) - 1)]

    def is_op(self, *ops) -> bool:
        kind, val = self.peek()
        return kind == "op" and val in ops

    def take(self):
        tok = self.peek()
        self.p += 1
        return tok

    def expect(self, op: str):
        if not self.is_op(op):
            raise self.unexpected()
        self.p += 1

    def unexpected(self) -> _Compile:
        kind, val = self.peek()
        return _Compile("syntax error, unexpected " + ("end of file" if kind == "eof" else repr(val if kind != "str" else '"')))

    def parse(self):
        node = self.pipe()
        if self.peek()[0] != "eof":
            raise self.unexpected()
        return node

    def pipe(self):
        if self.is_op("def"):
            raise _Compile("`def` is not supported")
        left = self.comma()
        if self.is_op("as"):
            self.p += 1
            pattern = self.pattern()
            self.expect("|")
            return ("as", left, pattern, self.pipe())
        if self.is_op("|"):
            self.p += 1
            return ("pipe", left, self.pipe())
        return left

    def pattern(self):
        kind, val = self.peek()
        if kind == "var":
            self.p += 1
            return ("var", val)
        if self.is_op("["):
            self.p += 1
            items = [self.pattern()]
            while self.is_op(","):
                self.p += 1
                items.append(self.pattern())
            self.expect("]")
            return ("array", items)
        if self.is_op("{"):
            self.p += 1
            entries = []
            while True:
                kind, val = self.peek()
                if kind == "var":
                    self.p += 1
                    entries.append((("lit", val), ("var", val)))
                else:
                    key = self.object_key()
                    self.expect(":")
                    entries.append((key, self.pattern()))
                if not self.is_op(","):
                    break
                self.p += 1
            self.expect("}")
            return ("object", entries)
        raise self.unexpected()

    def comma(self):
        left = self.alternative()
        while self.is_op(","):
            self.p += 1
            left = ("comma", left, self.alternative())
        return left

    def alternative(self):
        left = self.assignment()
        if self.is_op("//"):
            self.p += 1
            return ("alt", left, self.alternative())
        return left

    def assignment(self):
        left = self.or_()
        if self.is_op("=", "|=", "+=", "-=", "*=", "/=", "%=", "//="):
            op = self.take()[1]
            return ("assign", op, left, self.alternative())
        return left

    def or_(self):
        left = self.and_()
        while self.is_op("or"):
            self.p += 1
            left = ("or", left, self.and_())
        return left

    def and_(self):
        left = self.comparison()
        while self.is_op("and"):
            self.p += 1
            left = ("and", left, self.comparison())
        return left

    def comparison(self):
        left = self.additive()
        if self.is_op("==", "!=", "<", "<=", ">", ">="):
            op = self.take()[1]
            return ("binop", op, left, self.additive())
        return left

    def additive(self):
        left = self.multiplicative()
        while self.is_op("+", "-"):
            op = self.take()[1]
            left = ("binop", op, left, self.multiplicative())
        return left

    def multiplicative(self):
        left = self.unary()
        while self.is_op("*", "/", "%"):
            op = self.take()[1]
            left = ("binop", op, left, self.unary())
        return left

    def unary(self):
        if self.is_op("-"):
            self.p += 1
            return ("neg", self.unary())
        return self.postfix()

    def postfix(self):
        node = self.primary()
        while True:
            kind, val = self.peek()
            if kind == "field":
                self.p += 1
                node = ("index", node, ("lit", val))
            elif self.is_op(".") and self.peek(1)[0] == "str":
                self.p += 1
                node = ("index", node, self.string())
            elif self.is_op("[") or (self.is_op(".") and self.peek(1) == ("op", "[")):
                if self.is_op("."):
                    self.p += 1
                node = self.bracket(node)
            elif self.is_op("?"):
                self.p += 1
                node = ("try", node, None)
            else:
                return node

    def bracket(self, target):
        self.expect("[")
        if self.is_op("]"):
            self.p += 1
            return ("iterate", target)
        if self.is_op(":"):
            self.p += 1
            hi = self.pipe()
            self.expect("]")
            return ("slice", target, None, hi)
        idx = self.pipe()
        if self.is_op(":"):
            self.p += 1
            hi = None if self.is_op("]") else self.pipe()
            self.expect("]")
            return ("slice", target, idx, hi)
        self.expect("]")
        return ("index", target, idx)

    def string(self, fmt: str | None = None):
        parts = self.take()[1]
        out = [p if isinstance(p, str) else ("node", _Parser(p[1]).parse()) for p in parts]
        if len(out) == 1 and isinstance(out[0], str) and fmt is None:
            return ("lit", out[0])
        return ("string", out, fmt)

    def object_key(self):
        kind, val = self.peek()
        if kind == "ident" or (kind == "op" and val in _KEYWORDS):
            self.p += 1
            return ("lit", val)
        if kind == "var":
            self.p += 1
            return ("var", val)
        if kind == "str":
            return self.string()
        if kind == "format" and self.peek(1)[0] == "str":
            self.p += 1
            return self.string(val)
        if self.is_op("("):
            self.p += 1
            key = self.pipe()
            self.expect(")")
            return key
        raise self.unexpected()

    def primary(self):
        kind, val = self.peek()
        if kind == "num":
            self.p += 1
            return ("lit", val)
        if kind == "str":
            return self.string()
        if kind == "format":
            self.p += 1
            return self.string(val) if self.peek()[0] == "str" else ("format", val)
        if kind == "field":
            self.p += 1
            return ("index", ("identity",), ("lit", val))
        if kind == "var":
            self.p += 1
            return ("var", val)
        if kind == "ident":
            self.p += 1
            if val in ("true", "false", "null"):
                return ("lit", {"true": True, "false": False, "null": None}[val])
            args = []
            if self.is_op("("):
                self.p += 1
                args.append(self.pipe())
                while self.is_op(";"):
                    self.p += 1
                    args.append(self.pipe())
                self.expect(")")
            return ("call", val, args)
        if kind != "op":
            raise self.unexpected()
        if val == ".":
            self.p += 1
            if self.peek()[0] == "str":
                return ("index", ("identity",), self.string())
            if self.is_op("["):
                return self.bracket(("identity",))
            return ("identity",)
        if val == "..":
            self.p += 1
            return ("call", "recurse", [])
        if val == "(":
            self.p += 1
            node = self.pipe()
            self.expect(")")
            return node
        if val == "[":
            self.p += 1
            if self.is_op("]"):
                self.p += 1
                return ("array", None)
            node = self.pipe()
            self.expect("]")
            return ("array", node)
        if val == "{":
            return self.object()
        if val == "if":
            return self.if_()
        if val == "try":
            self.p += 1
            body = self.postfix()
            handler = None
            if self.is_op("catch"):
                self.p += 1
                handler = self.postfix()
            return ("try", body, handler)
        if val in ("reduce", "foreach"):
            self.p += 1
            source = self.postfix()
            self.expect("as")
            pattern = self.pattern()
            self.expect("(")
            init = self.pipe()
            self.expect(";")
            update = self.pipe()
            extract = None
            if val == "foreach" and self.is_op(";"):
                self.p += 1
                extract = self.pipe()
            self.expect(")")
            return (val, source, pattern, init, update, extract)
        raise self.unexpected()

    def if_(self):
        self.p += 1
        cond = self.pipe()
        self.expect("then")
        then = self.pipe()
        if self.is_op("elif"):
            return ("if", cond, then, self.if_())
        other = None
        if self.is_op("else"):
            self.p += 1
            other = self.pipe()
        self.expect("end")
        return ("if", cond, then, other)

    def object(self):
        self.expect("{")
        entries = []
        while not self.is_op("}"):
            key = self.object_key()
            if self.is_op(":"):
                self.p += 1
                value = self.alternative()
                while self.is_op("|"):
                    self.p += 1
                    value = ("pipe", value, self.alternative())
            elif key[0] == "var":
                value = key
                key = ("lit", key[1])
            else:
                value = ("index", ("identity",), key)
            entries.append((key, value))
            if not self.is_op(","):
                break
            self.p += 1
        self.expect("}")
        return ("object", entries)


# ---------- evaluation helpers ----------

def _brief(v) -> str:
    text = dump(v)
    return text if len(text) <= 11 else text[:10] + "..."


def _err_type(v) -> str:
    return f"{type_name(v)} ({_brief(v)})"


def _index(v, k):
    if isinstance(k, str):
        if v is None:
            return None
        if isinstance(v, dict):
            return v.get(k)
        raise JqError(f'Cannot index {type_name(v)} with "{k}"')
    if isinstance(k, float):
        if v is None:
            return None
        if isinstance(v, list):
            i = int(math.floor(k))
            i = i + len(v) if i < 0 else i
            return v[i] if 0 <= i < len(v) else None
        raise JqError(f"Cannot index {type_name(v)} with number")
    if isinstance(k, dict) and isinstance(v, (list, str)) or (isinstance(k, dict) and v is None):
        return _slice(v, k.get("start"), k.get("end"))
    if isinstance(k, list) and isinstance(v, list):
        return _indices(v, k)
    raise JqError(f"Cannot index {type_name(v)} with {type_name(k)}")


def _slice_bounds(n: int, lo, hi) -> tuple[int, int]:
    def clamp(x, default):
        if x is None:
            return default
        if not isinstance(x, float):
            raise JqError("Start and end indices of an array slice must be numbers")
        i = int(math.floor(x))
        return max(0, min(n, i + n if i < 0 else i))

    a, b = clamp(lo, 0), clamp(hi, n)
    return a, max(a, b)


def _slice(v, lo, hi):
    if v is None:
        return None
    if not isinstance(v, (list, str)):
        raise JqError(f"Cannot index {type_name(v)} with object")
    a, b = _slice_bounds(len(v), lo, hi)
    return v[a:b]


def _indices(v, sub):
    if v is None:
        return None
    if isinstance(v, str) and isinstance(sub, str):
        return [float(i) for i in range(len(v)) if v.startswith(sub, i)] if sub else None
    if isinstance(v, list):
        sub = sub if isinstance(sub, list) else [sub]
        if not sub:
            return None
        return [float(i) for i in range(len(v) - len(sub) + 1) if all(compare(x, y) == 0 for x, y in zip(v[i:], sub))]
    raise JqError(f"Cannot determine indices of {type_name(sub)} in {type_name(v)}")


def _iterate(v) -> list:
    if isinstance(v, list):
        return v
    if isinstance(v, dict):
        return list(v.values())
    raise JqError(f"Cannot iterate over {type_name(v)}" + ("" if v is None else f" ({_brief(v)})"))


def _arith(op: str, a, b):
    if op == "+":
        if a is None:
            return b
        if b is None:
            return a
        if type_name(a) == type_name(b) and isinstance(a, (float, str, list)):
            return a + b
        if isinstance(a, dict) and isinstance(b, dict):
            return {**a, **b}
    elif op == "-":
        if isinstance(a, float) and isinstance(b, float):
            return a - b
        if isinstance(a, list) and isinstance(b, list):
            return [x for x in a if all(compare(x, y) != 0 for y in b)]
    elif op == "*":
        if isinstance(a, float) and isinstance(b, float):
            return a * b
        if isinstance(a, str) and isinstance(b, float) or isinstance(b, str) and isinstance(a, float):
            s, k = (a, b) if isinstance(a, str) else (b, a)
            return s * math.ceil(k) if k > 0 else None
        if isinstance(a, dict) and isinstance(b, dict):
            return _deep_merge(a, b)
    elif op in ("/", "%"):
        if isinstance(a, float) and isinstance(b, float):
            if (b == 0) if op == "/" else (int(b) == 0):
                raise JqError(f"{_err_type(a)} and {_err_type(b)} cannot be divided because the divisor is zero")
            return a / b if op == "/" else float(int(math.fmod(int(a), int(b))))
        if op == "/" and isinstance(a, str) and isinstance(b, str):
            return _split(a, b)
    verb = {"+": "added", "-": "subtracted", "*": "multiplied", "/": "divided", "%": "divided"}[op]
    raise JqError(f"{_err_type(a)} and {_err_type(b)} cannot be {verb}")


def _deep_merge(a: dict, b: dict) -> dict:
    out = dict(a)
    for k, v in b.items():
        out[k] = _deep_merge(out[k], v) if isinstance(out.get(k), dict) and isinstance(v, dict) else v
    return out


def _split(s: str, sep: str) -> list:
    if s == "":
        return []
    return s.split(sep) if sep else list(s)


def _tostring(v) -> str:
    return v if isinstance(v, str) else dump(v)


def _regex(pattern, flags) -> tuple[re.Pattern, bool]:
    if not isinstance(pattern, str):
        raise JqError(f"{_err_type(pattern)} cannot be matched, as it is not a string")
    if flags is not None and not isinstance(flags, str):
        raise JqError(f"{_err_type(flags)} is not a string")
    f = 0
    for c in flags or "":
        if c == "i":
            f |= re.IGNORECASE
        elif c == "x":
            f |= re.VERBOSE
        elif c == "s":
            f |= re.DOTALL
        elif c not in "gnlp":
            raise JqError(f"{flags} is not a valid modifier string")
    try:
        # Oniguruma's (?<name>...) is Python's (?P<name>...)
        return re.compile(re.sub(r"\(\?<([A-Za-z_]\w*)>", r"(?P<\1>", pattern), f), "g" in (flags or "")
    except re.error as e:
        raise JqError(f"{pattern} (at offset {e.pos or 0}) is not a valid regex: {e.msg}") from None


def _matches(rx: re.Pattern, s: str, glob: bool) -> list[re.Match]:
    if glob:
        return list(rx.finditer(s))
    m = rx.search(s)
    return [m] if m else []


def _match_obj(m: re.Match) -> dict:
    names = {i: name for name, i in m.re.groupindex.items()}
    caps = []
    for g in range(1, m.re.groups + 1):
        text = m.group(g)
        caps.append({"offset": float(m.start(g)) if text is not None else -1.0, "length": float(len(text or "")),
                     "string": text, "name": names.get(g)})
    return {"offset": float(m.start()), "length": float(len(m.group(0))), "string": m.group(0), "captures": caps}


def _format(fmt: str, v) -> str:
    if fmt == "text":
        return _tostring(v)
    if fmt == "json":
        return dump(v)
    if fmt in ("csv", "tsv"):
        if not isinstance(v, list):
            raise JqError(f"{_err_type(v)} cannot be {fmt}-formatted, only an array can be")
        cells = []
        for x in v:
            if isinstance(x, (dict, list)):
                raise JqError(f"{_err_type(x)} is not valid in a csv row")
            if not isinstance(x, str):
                cells.append("" if x is None else _tostring(x))
            elif fmt == "csv":
                cells.append('"' + x.replace('"', '""') + '"')
            else:
                cells.append(x.replace("\\", "\\\\").replace("\t", "\\t").replace("\n", "\\n").replace("\r", "\\r"))
        return ("," if fmt == "csv" else "\t").join(cells)
    if fmt == "html":
        s = _tostring(v)
        for a, b in (("&", "&amp;"), ("<", "&lt;"), (">", "&gt;"), ("'", "&#39;"), ('"', "&quot;")):
            s = s.replace(a, b)
        return s
    if fmt == "uri":
        return quote(_tostring(v), safe="-_.~")
    if fmt == "sh":
        out = []
        for x in v if isinstance(v, list) else [v]:
            if isinstance(x, (dict, list)):
                raise JqError(f"{_err_type(x)} can not be escaped for shell")
            out.append("'" + x.replace("'", "'\\''") + "'" if isinstance(x, str) else _tostring(x))
        return " ".join(out)
    if fmt == "base64":
        return base64.b64encode(_tostring(v).encode()).decode()
    if fmt == "base64d":
        s = _tostring(v).rstrip("=")
        try:
            return base64.b64decode(s + "=" * (-len(s) % 4)).decode("utf-8", errors="replace")
        except ValueError:
            raise JqError(f"{_err_type(v)} is not valid base64 data") from None
    raise JqError(f"{fmt} is not a valid format")


def _all_paths(v, leaf_only=False, prefix=()):
    if isinstance(v, dict):
        items = list(v.items())
    elif isinstance(v, list):
        items = [(float(i), x) for i, x in enumerate(v)]
    else:
        return
    for k, x in items:
        p = prefix + (k,)
        if not leaf_only or not isinstance(x, (dict, list)):
            yield list(p)
        yield from _all_paths(x, leaf_only, p)


def _recurse(v):
    yield v
    if isinstance(v, (list, dict)):
        for x in (v if isinstance(v, list) else list(v.values())):
            yield from _recurse(x)


def getpath(v, path):
    if not isinstance(path, list):
        raise JqError("Path must be specified as an array")
    for k in path:
        if v is None:
            return None
        v = _index(v, k)
    return v


def setpath(v, path, value):
    if not isinstance(path, list):
        raise JqError("Path must be specified as an array")
    if not path:
        return value
    k, rest = path[0], path[1:]
    if isinstance(k, str):
        if v is None:
            v = {}
        if not isinstance(v, dict):
            raise JqError(f'Cannot index {type_name(v)} with "{k}"')
        out = dict(v)
        out[k] = setpath(v.get(k), rest, value)
        return out
    if isinstance(k, float):
        if v is None:
            v = []
        if not isinstance(v, list):
            raise JqError(f"Cannot index {type_name(v)} with number")
        i = int(k)
        if i < 0:
            i += len(v)
            if i < 0:
                raise JqError("Out of bounds negative array index")
        out = list(v) + [None] * max(0, i + 1 - len(v))
        out[i] = setpath(out[i], rest, value)
        return out
    if isinstance(k, dict):
        if v is None:
            v = []
        if not isinstance(v, list):
            raise JqError(f"Cannot update field at object index of {type_name(v)}")
        a, b = _slice_bounds(len(v), k.get("start"), k.get("end"))
        repl = setpath(v[a:b], rest, value)
        if not isinstance(repl, list):
            raise JqError("A slice of an array can only be assigned another array")
        return v[:a] + repl + v[b:]
    raise JqError(f"Invalid path component {dump(k)}")


def delpaths(v, paths):
    for p in sorted(paths, key=_sort_key, reverse=True):
        v = _delpath(v, p)
    return v


def _delpath(v, path):
    if not path:
        return None
    if v is None:
        return None
    k, rest = path[0], path[1:]
    if rest:
        child = _index(v, k)
        return v if child is None else setpath(v, [k], _delpath(child, rest))
    if isinstance(k, str):
        if not isinstance(v, dict):
            raise JqError(f"Cannot delete field at object index of {type_name(v)}")
        return {key: x for key, x in v.items() if key != k}
    if isinstance(k, float):
        if not isinstance(v, list):
            raise JqError(f"Cannot delete field at index of {type_name(v)}")
        i = int(k) + (len(v) if k < 0 else 0)
        return v[:i] + v[i + 1:] if 0 <= i < len(v) else v
    if isinstance(k, dict) and isinstance(v, list):
        a, b = _slice_bounds(len(v), k.get("start"), k.get("end"))
        return v[:a] + v[b:]
    raise JqError(f"Invalid path component {dump(k)}")


def _to_entries(v):
    if isinstance(v, dict):
        return [{"key": k, "value": x} for k, x in v.items()]
    if isinstance(v, list):
        return [{"key": float(i), "value": x} for i, x in enumerate(v)]
    raise JqError(f"{_err_type(v)} has no keys")


def _from_entries(entries):
    out = {}
    for e in _iterate(entries):
        if not isinstance(e, dict):
            raise JqError(f'Cannot index {type_name(e)} with "key"')
        k = next((e[n] for n in ("key", "k", "name", "Name", "Key", "K") if e.get(n) is not None), None)
        if isinstance(k, float):
            k = fmt_number(k)
        elif isinstance(k, bool):
            k = "true" if k else "false"
        elif k is None:
            k = "null"
        if not isinstance(k, str):
            raise JqError(f"Cannot use {_err_type(k)} as object key")
        out[k] = next((e[n] for n in ("value", "v", "Value", "V") if n in e), None)
    return out


def _contains(a, b) -> bool:
    if isinstance(a, dict) and isinstance(b, dict):
        return all(k in a and _contains(a[k], x) for k, x in b.items())
    if isinstance(a, list) and isinstance(b, list):
        return all(any(_contains(x, y) for x in a) for y in b)
    if isinstance(a, str) and isinstance(b, str):
        return b in a
    if type_name(a) == type_name(b):
        return compare(a, b) == 0
    raise JqError(f"{_err_type(a)} and {_err_type(b)} cannot have their containment checked")


def _length(v):
    if v is None:
        return 0.0
    if isinstance(v, bool):
        raise JqError(f"{_err_type(v)} has no length")
    if isinstance(v, float):
        return abs(v)
    return float(len(v))


def _keys(v, sort=True):
    if isinstance(v, dict):
        return sorted(v) if sort else list(v)
    if isinstance(v, list):
        return [float(i) for i in range(len(v))]
    raise JqError(f"{_err_type(v)} has no keys")


def _add(v):
    acc = None
    for x in ([] if v is None else _iterate(v)):
        acc = _arith("+", acc, x)
    return acc


def _flatten(v, depth=1e9):
    if not isinstance(v, list):
        raise JqError(f"Cannot iterate over {type_name(v)}")
    if depth < 0:
        raise JqError("flatten depth must not be negative")
    out = []
    for x in v:
        if isinstance(x, list) and depth > 0:
            out.extend(_flatten(x, depth - 1))
        else:
            out.append(x)
    return out


_NUMBER = re.compile(r"\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*")


def _tonumber(v):
    if isinstance(v, float):
        return v
    if isinstance(v, str):
        if _NUMBER.fullmatch(v):
            return float(v)
        raise JqError(f"Cannot parse '{v}' as JSON")
    raise JqError(f"{_err_type(v)} cannot be parsed as a number")


def _sorted(v):
    if not isinstance(v, list):
        raise JqError(f"{_err_type(v)} cannot be sorted, as it is not an array")
    return sorted(v, key=_sort_key)


def _unique(items):
    out = []
    for x in items:
        if not out or compare(out[-1], x) != 0:
            out.append(x)
    return out


def _string_arg(name: str, v) -> str:
    if not isinstance(v, str):
        raise JqError(f"{_err_type(v)} cannot be {name}")
    return v


def _number_arg(v) -> float:
    if not isinstance(v, float):
        raise JqError(f"{_err_type(v)} number required")
    return v


def _ascii_case(s: str, upper: bool) -> str:
    return "".join((c.upper() if upper else c.lower()) if ("a" <= c <= "z" if upper else "A" <= c <= "Z") else c for c in s)


def _todate(v):
    try:
        return time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(int(math.floor(_number_arg(v)))))
    except (OverflowError, OSError, ValueError):
        raise JqError(f"{_err_type(v)} is out of range for a date") from None


def _fromdate(v):
    try:
        return float(calendar.timegm(time.strptime(_string_arg("parsed as a date", v), "%Y-%m-%dT%H:%M:%SZ")))
    except ValueError:
        raise JqError(f'date "{v}" does not match format "%Y-%m-%dT%H:%M:%SZ"') from None


def _round(x: float) -> float:
    return float(math.floor(x + 0.5) if x >= 0 else -math.floor(-x + 0.5))


def _math(fn):
    return lambda v: [float(fn(_number_arg(v)))]


_BUILTINS_0 = {
    "length": lambda v: [_length(v)],
    "utf8bytelength": lambda v: [float(len(_string_arg("measured in UTF-8 bytes", v).encode()))],
    "not": lambda v: [not _truthy(v)],
    "keys": lambda v: [_keys(v)],
    "keys_unsorted": lambda v: [_keys(v, sort=False)],
    "values": lambda v: [v] if v is not None else [],
    "to_entries": lambda v: [_to_entries(v)],
    "from_entries": lambda v: [_from_entries(v)],
    "add": lambda v: [_add(v)],
    "any": lambda v: [any(_truthy(x) for x in _iterate(v))],
    "all": lambda v: [all(_truthy(x) for x in _iterate(v))],
    "flatten": lambda v: [_flatten(v)],
    "floor": _math(math.floor),
    "ceil": _math(math.ceil),
    "round": _math(_round),
    "sqrt": _math(math.sqrt),
    "fabs": _math(abs),
    "abs": _math(abs),
    "tostring": lambda v: [_tostring(v)],
    "tonumber": lambda v: [_tonumber(v)],
    "tojson": lambda v: [dump(v)],
    "fromjson": lambda v: [_fromjson(v)],
    "type": lambda v: [type_name(v)],
    "infinite": lambda v: [math.inf],
    "nan": lambda v: [math.nan],
    "isinfinite": lambda v: [math.isinf(_number_arg(v))],
    "isnan": lambda v: [math.isnan(_number_arg(v))],
    "isnormal": lambda v: [math.isfinite(_number_arg(v)) and v != 0],
    "sort": lambda v: [_sorted(v)],
    "unique": lambda v: [_unique(_sorted(v))],
    "reverse": lambda v: [[] if v is None else v[::-1] if isinstance(v, (list, str)) else _fail(f"Cannot reverse {_err_type(v)}")],
    "min": lambda v: [_sorted(v)[0] if v else None],
    "max": lambda v: [_sorted(v)[-1] if v else None],
    "ascii_downcase": lambda v: [_ascii_case(_string_arg("lowercased", v), False)],
    "ascii_upcase": lambda v: [_ascii_case(_string_arg("uppercased", v), True)],
    "ltrimstr": None, "rtrimstr": None,
    "trim": lambda v: [_string_arg("trimmed", v).strip(" \t\n\r\f\v")],
    "ltrim": lambda v: [_string_arg("trimmed", v).lstrip(" \t\n\r\f\v")],
    "rtrim": lambda v: [_string_arg("trimmed", v).rstrip(" \t\n\r\f\v")],
    "explode": lambda v: [[float(ord(c)) for c in _string_arg("exploded", v)]],
    "implode": lambda v: ["".join(chr(int(_number_arg(c))) for c in _iterate(v))],
    "first": lambda v: [_index(v, 0.0)],
    "last": lambda v: [_index(v, -1.0)],
    "paths": lambda v: list(_all_paths(v)),
    "leaf_paths": lambda v: list(_all_paths(v, leaf_only=True)),
    "arrays": lambda v: [v] if isinstance(v, list) else [],
    "objects": lambda v: [v] if isinstance(v, dict) else [],
    "iterables": lambda v: [v] if isinstance(v, (list, dict)) else [],
    "booleans": lambda v: [v] if isinstance(v, bool) else [],
    "numbers": lambda v: [v] if isinstance(v, float) else [],
    "strings": lambda v: [v] if isinstance(v, str) else [],
    "nulls": lambda v: [v] if v is None else [],
    "scalars": lambda v: [v] if not isinstance(v, (list, dict)) else [],
    "recurse": lambda v: list(_recurse(v)),
    "empty": lambda v: [],
    "error": lambda v: _fail(v),
    "now": lambda v: [time.time()],
    "todate": lambda v: [_todate(v)],
    "todateiso8601": lambda v: [_todate(v)],
    "fromdate": lambda v: [_fromdate(v)],
    "fromdateiso8601": lambda v: [_fromdate(v)],
    "input_filename": lambda v: [None],
}
del _BUILTINS_0["ltrimstr"], _BUILTINS_0["rtrimstr"]


def _fail(value):
    raise JqError(value)


def _fromjson(v):
    try:
        values = parse_json_stream(_string_arg("parsed as JSON", v))
    except ShellError:
        raise JqError(f"{v} (while parsing '{v}')") from None
    if len(values) != 1:
        raise JqError(f"Unexpected extra JSON values (while parsing '{v}')")
    return values[0]


class Interpreter:
    def __init__(self, env: dict):
        self.env = env

    def ev(self, node, v, vs):
        """Yields every output of node applied to input v; vs holds the bound $variables."""
        kind = node[0]
        if kind == "identity":
            yield v
        elif kind == "lit":
            yield node[1]
        elif kind == "pipe":
            for x in self.ev(node[1], v, vs):
                yield from self.ev(node[2], x, vs)
        elif kind == "comma":
            yield from self.ev(node[1], v, vs)
            yield from self.ev(node[2], v, vs)
        elif kind == "index":
            for t in self.ev(node[1], v, vs):
                for k in self.ev(node[2], v, vs):
                    yield _index(t, k)
        elif kind == "slice":
            for t in self.ev(node[1], v, vs):
                for lo in ([None] if node[2] is None else self.ev(node[2], v, vs)):
                    for hi in ([None] if node[3] is None else self.ev(node[3], v, vs)):
                        yield _slice(t, lo, hi)
        elif kind == "iterate":
            for t in self.ev(node[1], v, vs):
                yield from _iterate(t)
        elif kind == "array":
            yield [] if node[1] is None else list(self.ev(node[1], v, vs))
        elif kind == "object":
            yield from self._object(node[1], 0, {}, v, vs)
        elif kind == "string":
            yield from self._string(node[1], 0, "", v, vs, node[2])
        elif kind == "format":
            yield _format(node[1], v)
        elif kind == "neg":
            for x in self.ev(node[1], v, vs):
                if not isinstance(x, float):
                    raise JqError(f"{_err_type(x)} cannot be negated")
                yield -x
        elif kind == "binop":
            op = node[1]
            for b in self.ev(node[3], v, vs):
                for a in self.ev(node[2], v, vs):
                    if op in ("+", "-", "*", "/", "%"):
                        yield _arith(op, a, b)
                    else:
                        c = compare(a, b)
                        yield {"==": c == 0, "!=": c != 0, "<": c < 0, "<=": c <= 0, ">": c > 0, ">=": c >= 0}[op]
        elif kind in ("and", "or"):
            for a in self.ev(node[1], v, vs):
                if _truthy(a) == (kind == "or"):
                    yield kind == "or"
                    continue
                for b in self.ev(node[2], v, vs):
                    yield _truthy(b)
        elif kind == "alt":
            found = False
            try:
                for x in self.ev(node[1], v, vs):
                    if _truthy(x):
                        found = True
                        yield x
            except JqError:
                pass
            if not found:
                yield from self.ev(node[2], v, vs)
        elif kind == "if":
            for c in self.ev(node[1], v, vs):
                if _truthy(c):
                    yield from self.ev(node[2], v, vs)
                elif node[3] is None:
                    yield v
                else:
                    yield from self.ev(node[3], v, vs)
        elif kind == "try":
            try:
                for x in self.ev(node[1], v, vs):
                    yield x
            except JqError as e:
                if node[2] is not None:
                    yield from self.ev(node[2], e.value, vs)
        elif kind == "var":
            name = node[1]
            if name in vs:
                yield vs[name]
            elif name == "ENV":
                yield dict(self.env)
            else:
                raise JqError(f"${name} is not defined")
        elif kind == "as":
            for x in self.ev(node[1], v, vs):
                for bound in self._bind(node[2], x, vs):
                    yield from self.ev(node[3], v, bound)
        elif kind == "reduce":
            _, source, pattern, init, update, _ = node
            for acc in self.ev(init, v, vs):
                for x in self.ev(source, v, vs):
                    for bound in self._bind(pattern, x, vs):
                        results = list(self.ev(update, acc, bound))
                        acc = results[-1] if results else None
                yield acc
        elif kind == "foreach":
            _, source, pattern, init, update, extract = node
            for acc in self.ev(init, v, vs):
                for x in self.ev(source, v, vs):
                    for bound in self._bind(pattern, x, vs):
                        for acc in self.ev(update, acc, bound):
                            yield from ([acc] if extract is None else self.ev(extract, acc, bound))
        elif kind == "assign":
            yield from self._assign(node[1], node[2], node[3], v, vs)
        elif kind == "call":
            yield from self._call(node[1], node[2], v, vs)
        else:
            raise JqError(f"cannot evaluate {kind}")

    def _object(self, entries, i, acc, v, vs):
        if i == len(entries):
            yield dict(acc)
            return
        key_node, value_node = entries[i]
        for k in self.ev(key_node, v, vs):
            if not isinstance(k, str):
                raise JqError(f"Object keys must be strings")
            for val in self.ev(value_node, v, vs):
                yield from self._object(entries, i + 1, {**acc, k: val}, v, vs)

    def _string(self, parts, i, acc, v, vs, fmt):
        if i == len(parts):
            yield acc
            return
        p = parts[i]
        if isinstance(p, str):
            yield from self._string(parts, i + 1, acc + p, v, vs, fmt)
            return
        for x in self.ev(p[1], v, vs):
            yield from self._string(parts, i + 1, acc + (_format(fmt, x) if fmt else _tostring(x)), v, vs, fmt)

    def _bind(self, pattern, value, vs):
        kind = pattern[0]
        if kind == "var":
            yield {**vs, pattern[1]: value}
        elif kind == "array":
            if value is not None and not isinstance(value, list):
                raise JqError(f"Cannot index {type_name(value)} with number")
            results = [vs]
            for i, sub in enumerate(pattern[1]):
                item = _index(value, float(i))
                results = [b for r in results for b in self._bind(sub, item, r)]
            yield from results
        else:
            results = [vs]
            for key_node, sub in pattern[1]:
                results = [b for r in results for k in self.ev(key_node, value, r) for b in self._bind(sub, _index(value, k), r)]
            yield from results

    # -- paths: (path, value) pairs for the left side of =, |=, del(), path() --

    def paths(self, node, v, vs):
        kind = node[0]
        if kind == "identity":
            yield [], v
        elif kind == "pipe":
            for p, x in self.paths(node[1], v, vs):
                for q, y in self.paths(node[2], x, vs):
                    yield p + q, y
        elif kind == "comma":
            yield from self.paths(node[1], v, vs)
            yield from self.paths(node[2], v, vs)
        elif kind == "index":
            for p, t in self.paths(node[1], v, vs):
                for k in self.ev(node[2], v, vs):
                    yield p + [k], _index(t, k)
        elif kind == "slice":
            for p, t in self.paths(node[1], v, vs):
                for lo in ([None] if node[2] is None else self.ev(node[2], v, vs)):
                    for hi in ([None] if node[3] is None else self.ev(node[3], v, vs)):
                        yield p + [{"start": lo, "end": hi}], _slice(t, lo, hi)
        elif kind == "iterate":
            for p, t in self.paths(node[1], v, vs):
                if isinstance(t, dict):
                    for k, x in t.items():
                        yield p + [k], x
                else:
                    for i, x in enumerate(_iterate(t)):
                        yield p + [float(i)], x
        elif kind == "if":
            for c in self.ev(node[1], v, vs):
                if _truthy(c):
                    yield from self.paths(node[2], v, vs)
                elif node[3] is None:
                    yield [], v
                else:
                    yield from self.paths(node[3], v, vs)
        elif kind == "alt":
            found = False
            try:
                for p, x in self.paths(node[1], v, vs):
                    if _truthy(x):
                        found = True
                        yield p, x
            except JqError:
                pass
            if not found:
                yield from self.paths(node[2], v, vs)
        elif kind == "try":
            try:
                for item in self.paths(node[1], v, vs):
                    yield item
            except JqError:
                pass
        elif kind == "as":
            for x in self.ev(node[1], v, vs):
                for bound in self._bind(node[2], x, vs):
                    yield from self.paths(node[3], v, bound)
        elif kind == "call":
            name, args = node[1], node[2]
            if name == "empty" and not args:
                return
            if name == "select" and len(args) == 1:
                for c in self.ev(args[0], v, vs):
                    if _truthy(c):
                        yield [], v
            elif name == "recurse" and not args:
                yield [], v
                for p in _all_paths(v):
                    yield p, getpath(v, p)
            elif name in ("first", "last") and not args:
                k = 0.0 if name == "first" else -1.0
                yield [k], _index(v, k)
            elif name == "getpath" and len(args) == 1:
                for p in self.ev(args[0], v, vs):
                    yield list(p), getpath(v, p)
            elif name in ("first", "last") and len(args) == 1:
                found = list(self.paths(args[0], v, vs))
                if found:
                    yield found[0] if name == "first" else found[-1]
            elif name in ("paths", "leaf_paths") and not args:
                for p in _all_paths(v, name == "leaf_paths"):
                    yield p, getpath(v, p)
            else:
                raise JqError(f"Invalid path expression with result {name}")
        else:
            raise JqError("Invalid path expression")

    def _assign(self, op, lhs, rhs, v, vs):
        if op == "|=":
            out = v
            for p, _ in list(self.paths(lhs, v, vs)):
                results = list(self.ev(rhs, getpath(out, p), vs))
                out = setpath(out, p, results[0]) if results else _delpath(out, p)
            yield out
            return
        for value in self.ev(rhs, v, vs):
            out = v
            for p, _ in list(self.paths(lhs, v, vs)):
                if op == "=":
                    new = value
                elif op == "//=":
                    cur = getpath(out, p)
                    new = cur if _truthy(cur) else value
                else:
                    new = _arith(op[0], getpath(out, p), value)
                out = setpath(out, p, new)
            yield out

    # -- builtins with arguments --

    def _cartesian(self, args, v, vs):
        results = [[]]
        for node in args:
            results = [r + [x] for r in results for x in self.ev(node, v, vs)]
        return results

    def _call(self, name, args, v, vs):
        n = len(args)
        if n == 0 and name in _BUILTINS_0:
            yield from _BUILTINS_0[name](v)
            return
        if n == 0 and name == "env":
            yield dict(self.env)
            return
        a = args
        key = f"{name}/{n}"
        if key == "select/1":
            for c in self.ev(a[0], v, vs):
                if _truthy(c):
                    yield v
        elif key == "map/1":
            yield [y for x in _iterate(v) for y in self.ev(a[0], x, vs)]
        elif key == "map_values/1":
            if isinstance(v, dict):
                out = {}
                for k, x in v.items():
                    ys = list(self.ev(a[0], x, vs))
                    if ys:
                        out[k] = ys[0]
                yield out
            else:
                yield [ys[0] for x in _iterate(v) for ys in [list(self.ev(a[0], x, vs))] if ys]
        elif key in ("sort_by/1", "group_by/1", "unique_by/1", "min_by/1", "max_by/1"):
            if not isinstance(v, list):
                raise JqError(f"Cannot index {type_name(v)} with number")
            keyed = sorted(((list(self.ev(a[0], x, vs)), x) for x in v), key=lambda kx: _sort_key(kx[0]))
            if name == "sort_by":
                yield [x for _, x in keyed]
            elif name in ("min_by", "max_by"):
                yield (keyed[0][1] if name == "min_by" else keyed[-1][1]) if keyed else None
            else:
                groups: list = []
                for k, x in keyed:
                    if groups and compare(groups[-1][0], k) == 0:
                        groups[-1][1].append(x)
                    else:
                        groups.append((k, [x]))
                yield [g for _, g in groups] if name == "group_by" else [g[0] for _, g in groups]
        elif key == "with_entries/1":
            yield _from_entries([y for e in _to_entries(v) for y in self.ev(a[0], e, vs)])
        elif key == "has/1":
            for k in self.ev(a[0], v, vs):
                if isinstance(v, dict) and isinstance(k, str):
                    yield k in v
                elif isinstance(v, list) and isinstance(k, float):
                    yield 0 <= k < len(v)
                else:
                    raise JqError(f"Cannot check whether {type_name(v)} has a {type_name(k)} key")
        elif key == "in/1":
            for o in self.ev(a[0], v, vs):
                yield from self._call("has", [("lit", v)], o, vs)
        elif key in ("contains/1", "inside/1"):
            for x in self.ev(a[0], v, vs):
                yield _contains(v, x) if name == "contains" else _contains(x, v)
        elif key in ("startswith/1", "endswith/1"):
            for x in self.ev(a[0], v, vs):
                if not isinstance(v, str) or not isinstance(x, str):
                    raise JqError(f"{name}() requires string inputs")
                yield v.startswith(x) if name == "startswith" else v.endswith(x)
        elif key in ("ltrimstr/1", "rtrimstr/1"):
            for x in self.ev(a[0], v, vs):
                if isinstance(v, str) and isinstance(x, str) and x and (v.startswith(x) if name == "ltrimstr" else v.endswith(x)):
                    yield v[len(x):] if name == "ltrimstr" else v[:-len(x)]
                else:
                    yield v
        elif key == "join/1":
            for sep in self.ev(a[0], v, vs):
                parts = []
                for x in _iterate(v):
                    if isinstance(x, (dict, list)):
                        raise JqError(f"Cannot join with {type_name(x)}")
                    parts.append("" if x is None else _tostring(x))
                if parts and not isinstance(sep, str):
                    raise JqError(f"{_err_type(sep)} and string cannot be added")
                yield (sep if isinstance(sep, str) else "").join(parts)
        elif key == "split/1":
            for sep in self.ev(a[0], v, vs):
                if not isinstance(v, str) or not isinstance(sep, str):
                    raise JqError("split input and separator must be strings")
                yield _split(v, sep)
        elif name in ("test", "match", "capture", "scan", "split", "splits", "sub", "gsub") and (
                (n in (1, 2) and name not in ("sub", "gsub")) or (n in (2, 3) and name in ("sub", "gsub"))):
            yield from self._regex_call(name, a, v, vs)
        elif key in ("index/1", "rindex/1", "indices/1"):
            for x in self.ev(a[0], v, vs):
                found = _indices(v, x)
                if name == "indices":
                    yield found
                else:
                    yield (found[0] if name == "index" else found[-1]) if found else None
        elif name == "range" and n in (1, 2, 3):
            for bounds in self._cartesian(a, v, vs):
                if any(not isinstance(b, float) for b in bounds):
                    raise JqError("Range bounds must be numeric")
                lo, hi, step = (0.0, bounds[0], 1.0) if n == 1 else (bounds[0], bounds[1], bounds[2] if n == 3 else 1.0)
                count = 0
                while (step > 0 and lo + count * step < hi) or (step < 0 and lo + count * step > hi):
                    yield lo + count * step
                    count += 1
        elif key in ("first/1", "last/1"):
            last = _MISSING
            for x in self.ev(a[0], v, vs):
                last = x
                if name == "first":
                    break
            if last is not _MISSING:
                yield last
        elif key == "nth/1":
            for k in self.ev(a[0], v, vs):
                yield _index(v, k)
        elif key == "nth/2":
            for k in self.ev(a[0], v, vs):
                if not isinstance(k, float) or k < 0:
                    raise JqError("Out of bounds negative array index")
                for i, x in enumerate(self.ev(a[1], v, vs)):
                    if i == int(k):
                        yield x
                        break
        elif key == "limit/2":
            for k in self.ev(a[0], v, vs):
                if not isinstance(k, float):
                    raise JqError("Invalid limit: must be a number")
                if k <= 0:
                    continue
                for i, x in enumerate(self.ev(a[1], v, vs)):
                    yield x
                    if i + 1 >= k:
                        break
        elif key in ("any/1", "all/1"):
            vals = [_truthy(y) for x in _iterate(v) for y in self.ev(a[0], x, vs)]
            yield any(vals) if name == "any" else all(vals)
        elif key in ("any/2", "all/2"):
            vals = [_truthy(y) for x in self.ev(a[0], v, vs) for y in self.ev(a[1], x, vs)]
            yield any(vals) if name == "any" else all(vals)
        elif key in ("IN/1", "IN/2"):
            source = [v] if n == 1 else list(self.ev(a[0], v, vs))
            for x in source:
                yield any(compare(x, y) == 0 for y in self.ev(a[-1], v, vs))
        elif key == "isempty/1":
            yield next(iter(self.ev(a[0], v, vs)), _MISSING) is _MISSING
        elif key in ("recurse/1", "recurse/2"):
            def walk(x, depth):
                if depth > 10_000:
                    raise JqError("recurse: too deep")
                yield x
                for y in self.ev(a[0], x, vs):
                    if n == 2 and not any(_truthy(c) for c in self.ev(a[1], y, vs)):
                        continue
                    yield from walk(y, depth + 1)
            yield from walk(v, 0)
        elif key == "walk/1":
            def walk(x):
                if isinstance(x, list):
                    x = [walk(y) for y in x]
                elif isinstance(x, dict):
                    x = {k: walk(y) for k, y in x.items()}
                out = list(self.ev(a[0], x, vs))
                return out[0] if out else None
            yield walk(v)
        elif key in ("until/2", "while/2", "repeat/1"):
            x, count = v, 0
            while True:
                count += 1
                if count > 100_000:
                    raise JqError(f"{name}: too many iterations")
                if name != "repeat":
                    cond = any(_truthy(c) for c in self.ev(a[0], x, vs))
                    if name == "until" and cond:
                        yield x
                        break
                    if name == "while":
                        if not cond:
                            break
                        yield x
                else:
                    yield x
                nxt = list(self.ev(a[-1], x, vs))
                if not nxt:
                    break
                x = nxt[-1]
        elif key == "flatten/1":
            for d in self.ev(a[0], v, vs):
                yield _flatten(v, _number_arg(d))
        elif key == "path/1":
            for p, _ in self.paths(a[0], v, vs):
                yield p
        elif key == "paths/1":
            for p in _all_paths(v):
                if any(_truthy(c) for c in self.ev(a[0], getpath(v, p), vs)):
                    yield p
        elif key == "del/1":
            yield delpaths(v, [p for p, _ in self.paths(a[0], v, vs)])
        elif key == "delpaths/1":
            for ps in self.ev(a[0], v, vs):
                yield delpaths(v, _iterate(ps))
        elif key == "getpath/1":
            for p in self.ev(a[0], v, vs):
                yield getpath(v, p)
        elif key == "setpath/2":
            for p in self.ev(a[0], v, vs):
                for x in self.ev(a[1], v, vs):
                    yield setpath(v, p, x)
        elif key == "to_entries/0":
            yield _to_entries(v)
        elif key == "error/1":
            for msg in self.ev(a[0], v, vs):
                raise JqError(msg)
        elif key in ("min_by/0", "max_by/0"):
            raise JqError(f"{key} is not defined")
        elif key in ("input/0", "inputs/0", "debug/0", "stderr/0", "input_line_number/0"):
            if name in ("debug", "stderr"):
                yield v
            else:
                raise JqError(f"{key} is not supported")
        elif key in ("tostream/0", "fromstream/1", "getpath/0", "splits/0", "ascii/0"):
            raise JqError(f"{key} is not supported")
        else:
            raise _Compile(f"{key} is not defined")

    def _regex_call(self, name, args, v, vs):
        if not isinstance(v, str):
            raise JqError(f"{_err_type(v)} cannot be matched, as it is not a string")
        if name in ("sub", "gsub"):
            for pattern in self.ev(args[0], v, vs):
                for flags in ([None] if len(args) < 3 else self.ev(args[2], v, vs)):
                    rx, glob = _regex(pattern, flags)
                    yield from self._substitute(rx, args[1], v, vs, glob or name == "gsub")
            return
        for combo in self._cartesian(args, v, vs):
            rx, glob = _regex(combo[0], combo[1] if len(combo) > 1 else None)
            if name == "test":
                yield rx.search(v) is not None
            elif name == "match":
                for m in _matches(rx, v, glob):
                    yield _match_obj(m)
            elif name == "capture":
                for m in _matches(rx, v, glob):
                    yield {k: m.group(k) for k in rx.groupindex}
            elif name == "scan":
                for m in rx.finditer(v):
                    yield m.group(0) if not rx.groups else list(m.groups())
            else:
                pieces, pos = [], 0
                for m in rx.finditer(v):
                    pieces.append(v[pos:m.start()])
                    pos = m.end()
                pieces.append(v[pos:])
                if name == "split":
                    yield pieces
                else:
                    yield from pieces

    def _substitute(self, rx, repl_node, v, vs, glob):
        results = [""]
        pos = 0
        for m in _matches(rx, v, glob):
            captures = {k: m.group(k) for k in rx.groupindex}
            reps = list(self.ev(repl_node, captures, vs))
            for r in reps:
                if not isinstance(r, str):
                    raise JqError(f"{_err_type(r)} cannot be added to a string")
            results = [acc + v[pos:m.start()] + r for acc in results for r in reps]
            pos = m.end()
        for acc in results:
            yield acc + v[pos:]


_MISSING = object()


def compile_filter(src: str):
    try:
        return _Parser(src).parse()
    except _Compile as e:
        raise ShellError(f"error: {e}\njq: 1 compile error", 3) from None
    except RecursionError:
        raise ShellError("error: filter is nested too deeply\njq: 1 compile error", 3) from None


_LONG_FLAGS = {"raw-output": "r", "compact-output": "c", "null-input": "n", "slurp": "s", "sort-keys": "S", "join-output": "j",
               "raw-input": "R", "exit-status": "e", "ascii-output": "a", "monochrome-output": "M", "color-output": "C",
               "tab": "t"}


def _jq(ctx):
    args = list(ctx.args)
    flags: set[str] = set()
    named: dict = {}
    extra: list = []
    filt: str | None = None
    files: list[str] = []
    indent = "  "
    rest_mode = None  # --args / --jsonargs: remaining positionals are $ARGS.positional
    i = 0
    while i < len(args):
        arg = args[i]
        i += 1
        if arg in ("--arg", "--argjson", "--slurpfile", "--rawfile"):
            if i + 1 >= len(args):
                raise ShellError(f"{arg} takes two parameters (e.g. {arg} varname value)", 2)
            name, value = args[i], args[i + 1]
            i += 2
            if arg == "--arg":
                named[name] = value
            elif arg == "--argjson":
                try:
                    values = parse_json_stream(value)
                except ShellError:
                    values = []
                if len(values) != 1:
                    raise ShellError(f"Invalid JSON text passed to --argjson", 2)
                named[name] = values[0]
            else:
                text = ctx.fs.read_text(ctx.resolve(value))
                named[name] = text if arg == "--rawfile" else parse_json_stream(text)
            continue
        if arg == "--indent":
            if i >= len(args) or not args[i].isdigit():
                raise ShellError("--indent takes one parameter", 2)
            indent = " " * min(int(args[i]), 7)
            i += 1
            continue
        if arg in ("--args", "--jsonargs"):
            rest_mode = arg
            continue
        if arg.startswith("--") and len(arg) > 2:
            if arg[2:] not in _LONG_FLAGS:
                raise ShellError(f"Unknown option: {arg}", 2)
            flags.add(_LONG_FLAGS[arg[2:]])
            continue
        if arg.startswith("-") and len(arg) > 1:
            for c in arg[1:]:
                if c not in "rcnsSjReaMC":
                    raise ShellError(f"Unknown option: {arg}", 2)
                flags.add(c)
            continue
        if filt is None:
            filt = arg
        elif rest_mode == "--args":
            extra.append(arg)
        elif rest_mode == "--jsonargs":
            values = parse_json_stream(arg)
            if len(values) != 1:
                raise ShellError(f"Invalid JSON text passed to --jsonargs", 2)
            extra.append(values[0])
        else:
            files.append(arg)
    if filt is None:
        raise ShellError("Usage: jq [OPTIONS] FILTER [FILES...]", 2)
    node = compile_filter(filt)
    code = 0

    def fail():
        nonlocal code
        code = 2

    if "n" in flags:
        docs = [None]
    else:
        texts = [t for _, t in inputs(ctx, files, fail)]
        if "R" in flags:
            raw = "".join(texts)
            docs = [raw] if "s" in flags else raw.split("\n")[:-1] if raw.endswith("\n") else raw.split("\n") if raw else []
        else:
            docs = [d for t in texts for d in parse_json_stream(t)]
            if "s" in flags:
                docs = [docs]
    variables = dict(named)
    variables["ARGS"] = {"positional": extra, "named": named}
    variables["__loc__"] = {"file": "<stdin>", "line": 1.0}
    unit = "" if "c" in flags else "\t" if "t" in flags else indent
    interp = Interpreter(ctx.shell.env)
    last = _MISSING
    out: list[str] = []
    for doc in docs:
        try:
            for result in interp.ev(node, doc, variables):
                last = result
                if ("r" in flags or "j" in flags) and isinstance(result, str):
                    text = result
                else:
                    text = dump(result, unit, "S" in flags, "a" in flags)
                out.append(text if "j" in flags else text + "\n")
        except JqError as e:
            msg = e.value if isinstance(e.value, str) else dump(e.value) + " (not a string)"
            out.append(f"\x00jq: error (at <unknown>): {msg}\n")
            code = 5
        except _Compile as e:
            raise ShellError(f"error: {e}\njq: 1 compile error", 3) from None
        except RecursionError:
            out.append("\x00jq: error: recursion too deep\n")
            code = 5
    for chunk in out:
        if chunk.startswith("\x00"):
            ctx.stderr.write(chunk[1:])
        else:
            ctx.stdout.write(chunk)
    if code == 0 and "e" in flags:
        return 4 if last is _MISSING else 0 if _truthy(last) else 1
    return code


def register(shell) -> None:
    shell.register_command("jq", _jq, "Command-line JSON processor")
