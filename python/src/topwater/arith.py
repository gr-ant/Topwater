"""Bash integer arithmetic: $(( )), (( )), let, for (( ; ; ))."""
from __future__ import annotations

import re

from .vfs import ShellError

_OPS = [
    "**=", "<<=", ">>=", "&&", "||", "==", "!=", "<=", ">=", "<<", ">>", "**", "++", "--", "+=", "-=", "*=", "/=", "%=",
    "&=", "|=", "^=", "+", "-", "*", "/", "%", "<", ">", "=", "!", "~", "&", "|", "^", "?", ":", "(", ")", ",",
]
_ASSIGN = {"=", "+=", "-=", "*=", "/=", "%=", "<<=", ">>=", "&=", "|=", "^=", "**="}
_LEVELS = [["||"], ["&&"], ["|"], ["^"], ["&"], ["==", "!="], ["<", "<=", ">", ">="], ["<<", ">>"], ["+", "-"], ["*", "/", "%"]]
_VAR_REF = re.compile(r"\$\{?([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[#?$])\}?")


def _tokenize(s: str) -> list[str]:
    tokens, i = [], 0
    while i < len(s):
        if s[i].isspace():
            i += 1
            continue
        if s[i].isalnum() or s[i] == "_":
            j = i
            while j < len(s) and (s[j].isalnum() or s[j] in "_#"):
                j += 1
            tokens.append(s[i:j])
            i = j
            continue
        op = next((o for o in _OPS if s.startswith(o, i)), None)
        if op is None:
            raise ShellError(f'{s}: syntax error: invalid arithmetic operator (error token is "{s[i:]}")')
        tokens.append(op)
        i += len(op)
    return tokens


def evaluate(shell, expr: str, depth: int = 0) -> int:
    if depth > 20:
        raise ShellError(f"{expr}: expression recursion level exceeded")
    expr = _VAR_REF.sub(lambda m: shell.get_variable(m.group(1)), expr)
    tokens = _tokenize(expr)
    if not tokens:
        return 0
    a = _Arith(shell, expr, tokens, depth)
    v = a.comma(True)
    if a.p < len(tokens):
        raise a.syntax(tokens[a.p])
    return v


def _cdiv(a: int, b: int) -> int:
    q = abs(a) // abs(b)
    return q if (a >= 0) == (b >= 0) else -q


def _cmod(a: int, b: int) -> int:
    return a - b * _cdiv(a, b)


class _Arith:
    def __init__(self, shell, src: str, tokens: list[str], depth: int):
        self.sh, self.src, self.t, self.p, self.depth = shell, src, tokens, 0, depth

    def syntax(self, token: str) -> ShellError:
        return ShellError(f'{self.src.strip()}: syntax error in expression (error token is "{token}")')

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
        v = self.sh.get_variable(name).strip()
        if not v:
            return 0
        try:
            return int(v)
        except ValueError:
            return evaluate(self.sh, v, self.depth + 1)

    def set(self, name: str, v: int) -> None:
        self.sh.env[name] = str(v)

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
            return self.unary(ev)
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
                raise ShellError(f"{self.src.strip()}: division by 0")
            return 0
        ops = {
            "+": lambda: l + r, "-": lambda: l - r, "*": lambda: l * r, "/": lambda: _cdiv(l, r), "%": lambda: _cmod(l, r),
            "**": lambda: l ** r if r >= 0 else 0, "<<": lambda: l << r, ">>": lambda: l >> r, "&": lambda: l & r,
            "|": lambda: l | r, "^": lambda: l ^ r, "<": lambda: int(l < r), "<=": lambda: int(l <= r), ">": lambda: int(l > r),
            ">=": lambda: int(l >= r), "==": lambda: int(l == r), "!=": lambda: int(l != r),
        }
        if op not in ops:
            raise self.syntax(op)
        return ops[op]()

    def unary(self, ev: bool) -> int:
        if self.accept("+"):
            return self.unary(ev)
        if self.accept("-"):
            return -self.unary(ev)
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
        b = self.postfix(ev)
        if self.accept("**"):
            e = self.unary(ev)
            return b ** e if e >= 0 else 0
        return b

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
            try:
                if tok.lower().startswith("0x"):
                    return int(tok[2:], 16)
                if "#" in tok:
                    base, digits = tok.split("#", 1)
                    return int(digits, int(base))
                if len(tok) > 1 and tok[0] == "0":
                    return int(tok, 8)
                return int(tok)
            except ValueError:
                raise ShellError(f'{tok}: value too great for base (error token is "{tok}")') from None
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
