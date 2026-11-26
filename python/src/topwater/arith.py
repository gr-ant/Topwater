"""Bash integer arithmetic: $(( )), (( )), let, for (( ; ; ))."""
from __future__ import annotations

import functools
import re

from ._spec import diagnostic
from .vfs import ShellError

_OPS = [
    "**=", "<<=", ">>=", "&&", "||", "==", "!=", "<=", ">=", "<<", ">>", "**", "++", "--", "+=", "-=", "*=", "/=", "%=",
    "&=", "|=", "^=", "+", "-", "*", "/", "%", "<", ">", "=", "!", "~", "&", "|", "^", "?", ":", "(", ")", ",",
]
_ASSIGN = {"=", "+=", "-=", "*=", "/=", "%=", "<<=", ">>=", "&=", "|=", "^=", "**="}
_LEVELS = [["||"], ["&&"], ["|"], ["^"], ["&"], ["==", "!="], ["<", "<=", ">", ">="], ["<<", ">>"], ["+", "-"], ["*", "/", "%"]]
_VAR_REF = re.compile(r"\$\{?([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[#?$])\}?")


@functools.lru_cache(maxsize=512)  # loops re-evaluate the same expressions; tokens are never mutated
def _tokenize(s: str) -> tuple[str, ...]:
    tokens, i = [], 0
    while i < len(s):
        if s[i].isspace():
            i += 1
            continue
        if s[i].isalnum() or s[i] == "_":
            j = i
            while j < len(s) and (s[j].isalnum() or s[j] in "_#"):
                j += 1
            # name[subscript] is one operand, so an element can be read, assigned and incremented anywhere
            if j < len(s) and s[j] == "[" and (s[i].isalpha() or s[i] == "_"):
                depth = 0
                while j < len(s):
                    if s[j] == "[":
                        depth += 1
                    elif s[j] == "]":
                        depth -= 1
                        if depth == 0:
                            j += 1
                            break
                    j += 1
                if depth > 0:
                    raise ShellError(diagnostic("arithmeticBadSubscript", expression=s))
            tokens.append(s[i:j])
            i = j
            continue
        op = next((o for o in _OPS if s.startswith(o, i)), None)
        if op is None:
            raise ShellError(diagnostic("invalidArithmeticOperator", expression=s, token=s[i:]))
        # `5--3` (after $a-$b is substituted) is two minus signs; `--` is one operator only next to a name
        next_is_name = i + 2 < len(s) and (s[i + 2].isalpha() or s[i + 2] == "_")
        if op in ("++", "--") and not next_is_name and not (tokens and _is_ident(tokens[-1])):
            op = op[0]
        tokens.append(op)
        i += len(op)
    return tuple(tokens)


def evaluate(shell, expr: str, depth: int = 0) -> int:
    if depth > 20:
        raise ShellError(diagnostic("recursionExceeded", expression=expr))
    if "$" in expr:
        expr = _VAR_REF.sub(lambda m: shell.get_variable(m.group(1)), expr)
    tokens = _tokenize(expr)
    if not tokens:
        return 0
    a = _Arith(shell, expr, tokens, depth)
    v = a.comma(True)
    if a.p < len(tokens):
        raise a.syntax(tokens[a.p])
    return v


def _is_ident(s: str) -> bool:
    return s[0].isalpha() or s[0] == "_"


def _wrap(x: int) -> int:
    """Bash arithmetic is signed 64-bit and wraps around."""
    x &= 0xFFFFFFFFFFFFFFFF
    return x - (1 << 64) if x >= 1 << 63 else x


def _literal(digits: str, radix: int, tok: str) -> int:
    """A literal in the given base (2-64: 0-9 a-z A-Z @ _); values past 64 bits wrap around like bash."""
    if not digits or not 2 <= radix <= 64:
        raise ShellError(diagnostic("invalidBase", token=tok))
    v = 0
    for c in digits:
        d = (ord(c) - 48 if c.isdigit() and c.isascii() else ord(c) - 87 if "a" <= c <= "z"
             else (ord(c) - 55 if radix <= 36 else ord(c) - 29) if "A" <= c <= "Z" else 62 if c == "@" else 63 if c == "_" else 99)
        if d >= radix:
            raise ShellError(diagnostic("valueTooGreat", token=tok))
        v = v * radix + d
    return _wrap(v)


def _cdiv(a: int, b: int) -> int:
    q = abs(a) // abs(b)
    return q if (a >= 0) == (b >= 0) else -q


def _cmod(a: int, b: int) -> int:
    return a - b * _cdiv(a, b)


class _Arith:
    def __init__(self, shell, src: str, tokens: list[str], depth: int):
        self.sh, self.src, self.t, self.p, self.depth = shell, src, tokens, 0, depth

    def syntax(self, token: str) -> ShellError:
        return ShellError(diagnostic("arithmeticSyntax", expression=self.src.strip(), token=token))

    @property
    def peek(self):
        return self.t[self.p] if self.p < len(self.t) else None

    def accept(self, s: str) -> bool:
        if self.peek != s:
            return False
        self.p += 1
        return True

    @staticmethod
    def is_ident(s: str) -> bool:
        return s[0].isalpha() or s[0] == "_"

    def var(self, name: str) -> int:
        """A variable or name[sub] element as a number: empty or unset is 0, other text is evaluated as an expression."""
        open_ = name.find("[")
        v = (self.sh.get_variable(name) if open_ < 0 else self.sh.get_element(name[:open_], name[open_ + 1:-1]) or "").strip()
        if not v:
            return 0
        # a value like 010 or 0x1f is a literal in its base, as if it were written in the expression
        if not (len(v.lstrip("+-")) > 1 and v.lstrip("+-")[0] == "0") and re.fullmatch(r"[+-]?[0-9]+", v):
            return int(v)
        return evaluate(self.sh, v, self.depth + 1)

    def set(self, name: str, v: int) -> None:
        self.sh.set_var(name, str(v))

    def comma(self, ev: bool) -> int:
        v = self.assign(ev)
        while self.accept(","):
            v = self.assign(ev)
        return v

    def assign(self, ev: bool) -> int:
        if self.p + 1 < len(self.t) and self.is_ident(self.t[self.p]) and self.t[self.p + 1] in _ASSIGN:
            name, op = self.t[self.p], self.t[self.p + 1]
            self.p += 2
            r = self.assign(ev)
            if not ev:
                return r
            v = r if op == "=" else self.apply(op[:-1], self.var(name), r, True)
            self.set(name, v)
            return v
        return self.ternary(ev)

    def ternary(self, ev: bool) -> int:
        c = self.binary(0, ev)
        if not self.accept("?"):
            return c
        a = self.assign(ev and c != 0)
        if not self.accept(":"):
            raise self.syntax(self.peek or "end of expression")
        b = self.assign(ev and c == 0)
        return a if c != 0 else b

    def binary(self, level: int, ev: bool) -> int:
        if level == len(_LEVELS):
            return self.power_(ev)
        left = self.binary(level + 1, ev)
        while self.peek in _LEVELS[level]:
            op = self.t[self.p]
            self.p += 1
            if op == "&&":
                right = self.binary(level + 1, ev and left != 0)
                left = int(left != 0 and right != 0)
            elif op == "||":
                right = self.binary(level + 1, ev and left == 0)
                left = int(left != 0 or right != 0)
            else:
                left = self.apply(op, left, self.binary(level + 1, ev), ev)
        return left

    def apply(self, op: str, l: int, r: int, ev: bool) -> int:
        if op in ("/", "%") and r == 0:
            if ev:
                raise ShellError(diagnostic("divisionByZero", expression=self.src.strip()))
            return 0
        ops = {
            "+": lambda: l + r, "-": lambda: l - r, "*": lambda: l * r, "/": lambda: _cdiv(l, r), "%": lambda: _cmod(l, r),
            "**": lambda: self.power(l, r), "<<": lambda: l << (r & 63), ">>": lambda: l >> (r & 63), "&": lambda: l & r,
            "|": lambda: l | r, "^": lambda: l ^ r, "<": lambda: int(l < r), "<=": lambda: int(l <= r), ">": lambda: int(l > r),
            ">=": lambda: int(l >= r), "==": lambda: int(l == r), "!=": lambda: int(l != r),
        }
        if op not in ops:
            raise self.syntax(op)
        return _wrap(ops[op]())

    def power(self, b: int, e: int) -> int:
        if e < 0:
            raise ShellError(diagnostic("negativeExponent", expression=self.src.strip(), token=str(e)))
        return _wrap(pow(b, e, 1 << 64))

    def unary(self, ev: bool) -> int:
        if self.accept("+"):
            return self.unary(ev)
        if self.accept("-"):
            return _wrap(-self.unary(ev))
        if self.accept("!"):
            return int(self.unary(ev) == 0)
        if self.accept("~"):
            return ~self.unary(ev)
        if self.peek in ("++", "--"):
            op = self.t[self.p]
            self.p += 1
            name = self.peek
            if name is None or not self.is_ident(name):
                raise self.syntax(name or op)
            self.p += 1
            if not ev:
                return 0
            v = self.var(name) + (1 if op == "++" else -1)
            self.set(name, v)
            return v
        return self.postfix(ev)

    def power_(self, ev: bool) -> int:
        """`**` binds looser than unary minus in bash: -2**2 is 4."""
        b = self.unary(ev)
        return self.power(b, self.power_(ev)) if self.accept("**") else b  # right-associative

    def postfix(self, ev: bool) -> int:
        if self.accept("("):
            v = self.comma(ev)
            if not self.accept(")"):
                raise self.syntax(self.peek or "end of expression")
            return v
        tok = self.peek
        if tok is None:
            raise self.syntax("end of expression (operand expected)")
        self.p += 1
        if tok[0].isdigit():
            if tok.lower().startswith("0x"):
                return _literal(tok[2:] or "0", 16, tok)
            if "#" in tok:
                base, digits = tok.split("#", 1)
                return _literal(digits, int(base) if base.isdigit() else 0, tok)
            return _literal(tok, 8 if len(tok) > 1 and tok[0] == "0" else 10, tok)
        if not self.is_ident(tok):
            raise self.syntax(tok)
        if self.peek in ("++", "--"):
            op = self.t[self.p]
            self.p += 1
            old = self.var(tok)
            if ev:
                self.set(tok, old + (1 if op == "++" else -1))
            return old
        return self.var(tok)
