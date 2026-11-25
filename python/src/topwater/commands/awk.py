"""awk: patterns, ranges, BEGIN/END, fields, associative arrays, if/while/do/for/for-in, print/printf (with > / >> files),
user-defined functions, getline (from the input or a file), RS (one character, a regex, or "" for paragraph mode) and
the common built-ins. Mirrored by Awk.cs.

shortcut: no pipes to/from commands (print | "cmd", "cmd" | getline); add them if agents need them.
"""
from __future__ import annotations

import math
import random
import re
from dataclasses import dataclass

from ..vfs import ShellError
from . import split_lines, unescape


class AwkError(ShellError):
    def __init__(self, msg: str):
        super().__init__(msg, 2)


# ---------- values: float = number, str = string, StrNum = input data, None = uninitialized ----------

class StrNum(str):
    pass


_NUM_RE = re.compile(r"^\s*[-+]?(\d+\.?\d*([eE][-+]?\d+)?|\.\d+([eE][-+]?\d+)?)\s*$")
_LEAD_RE = re.compile(r"^\s*[-+]?(\d+\.?\d*([eE][-+]?\d+)?|\.\d+([eE][-+]?\d+)?)")


def to_num(v) -> float:
    if v is None:
        return 0.0
    if isinstance(v, float):
        return v
    m = _LEAD_RE.match(v)
    return float(m.group(0)) if m else 0.0


def fmt_number(v: float, fmt: str = "%.6g") -> str:
    if math.isfinite(v) and v == math.floor(v):
        return str(int(v))  # like gawk, integral values print as integers however large
    return fmt % v


def to_str(v) -> str:
    if v is None:
        return ""
    if isinstance(v, float):
        return fmt_number(v)
    return v


def is_numeric(v) -> bool:
    return v is None or isinstance(v, float) or (isinstance(v, StrNum) and bool(_NUM_RE.match(v)))


def truthy(v) -> bool:
    if v is None:
        return False
    if isinstance(v, float):
        return v != 0
    if isinstance(v, StrNum) and _NUM_RE.match(v):
        return to_num(v) != 0
    return v != ""


def compare(a, b) -> int:
    if is_numeric(a) and is_numeric(b):
        x, y = to_num(a), to_num(b)
    else:
        x, y = to_str(a), to_str(b)
    return (x > y) - (x < y)


_SPEC = re.compile(r"%([-+ #0]*)(\*|\d+)?(?:\.(\*|\d*))?([diouxXeEfFgGcs%])")


def sprintf(fmt: str, args: list) -> str:
    it = iter(args)

    def nxt():
        return next(it, None)

    def sub(m: re.Match) -> str:
        flags, width, prec, conv = m.group(1), m.group(2), m.group(3), m.group(4)
        if conv == "%":
            return "%"
        if width == "*":
            w = int(to_num(nxt()))
            if w < 0:
                flags, w = flags + "-", -w
            width = str(w)
        if prec == "*":
            prec = str(int(to_num(nxt())))
        spec = "%" + flags + (width or "") + ("." + (prec or "0") if prec is not None else "")
        v = nxt()
        if conv in "diouxX":
            n = to_num(v)
            return (spec + ("d" if conv in "iu" else conv)) % (0 if math.isnan(n) else int(n))
        if conv in "eEfFgG":
            return (spec + conv) % to_num(v)
        if conv == "c":
            ch = chr(int(v)) if isinstance(v, float) else to_str(v)[:1]
            return (spec + "s") % ch
        return (spec + "s") % to_str(v)

    return _SPEC.sub(sub, fmt)


# ---------- AST ----------

@dataclass
class Num:
    v: float


@dataclass
class Str:
    v: str


@dataclass
class Rx:
    pattern: str


@dataclass
class Var:
    name: str


@dataclass
class Field:
    index: object


@dataclass
class Index:
    name: str
    keys: list


@dataclass
class Assign:
    target: object
    op: str
    value: object


@dataclass
class Cond:
    c: object
    a: object
    b: object


@dataclass
class Bin:
    op: str
    l: object
    r: object


@dataclass
class Un:
    op: str
    e: object


@dataclass
class Incr:
    target: object
    inc: bool
    prefix: bool


@dataclass
class Match:
    s: object
    rx: object
    negate: bool


@dataclass
class In:
    keys: list
    array: str


@dataclass
class Concat:
    l: object
    r: object


@dataclass
class Call:
    name: str
    args: list


@dataclass
class UserCall:
    name: str
    args: list


@dataclass
class Getline:
    """getline [var] [< file]; cmd is set for `cmd | getline`."""
    target: object
    file: object
    cmd: object = None


@dataclass
class ReturnS:
    value: object


@dataclass
class Block:
    body: list


@dataclass
class ExprS:
    e: object


@dataclass
class PrintS:
    printf: bool
    args: list
    redir: str | None
    dest: object


@dataclass
class IfS:
    c: object
    then: object
    else_: object


@dataclass
class WhileS:
    c: object
    body: object
    do_while: bool


@dataclass
class ForS:
    init: object
    c: object
    step: object
    body: object


@dataclass
class ForInS:
    var: str
    array: str
    body: object


@dataclass
class SimpleS:
    kind: str
    arg: object = None


@dataclass
class DeleteS:
    array: str
    keys: list | None


@dataclass
class Rule:
    kind: str = "main"
    pattern: object = None
    pattern2: object = None
    action: Block | None = None
    in_range: bool = False


# ---------- lexer / parser ----------

_KEYWORDS = {"BEGIN", "END", "if", "else", "while", "for", "do", "break", "continue", "next", "nextfile", "exit", "delete",
             "in", "print", "printf", "getline", "function", "func", "return"}
_FUNCS = {"length", "substr", "index", "split", "sub", "gsub", "match", "sprintf", "tolower", "toupper", "int", "sqrt",
          "exp", "log", "sin", "cos", "atan2", "rand", "srand", "system", "close", "fflush"}
_OPS = ["**=", "+=", "-=", "*=", "/=", "%=", "^=", "**", "==", "<=", ">=", "!=", "++", "--", "&&", "||", ">>", "!~",
        "{", "}", "(", ")", "[", "]", ";", ",", "+", "-", "*", "/", "%", "^", "!", ">", "<", "|", "?", ":", "~", "$", "="]
_ASSIGN_OPS = {"=", "+=", "-=", "*=", "/=", "%=", "^=", "**="}
_STR_ESC = {"n": "\n", "t": "\t", "r": "\r", "\\": "\\", '"': '"', "/": "/", "a": "\a", "b": "\b", "f": "\f", "v": "\v"}


@dataclass
class Tok:
    kind: str  # num str regex name func kw op nl eof
    text: str
    num: float = 0.0


def _lex(s: str) -> list[Tok]:
    toks: list[Tok] = []

    def regex_allowed() -> bool:
        if not toks:
            return True
        t = toks[-1]
        if t.kind in ("num", "str", "name", "regex"):
            return False
        if t.kind == "func" and t.text == "length":
            return False
        return not (t.kind == "op" and t.text in (")", "]", "$", "++", "--"))

    i = 0
    while i < len(s):
        c = s[i]
        if c in " \t\r":
            i += 1
        elif c == "\\" and i + 1 < len(s) and s[i + 1] == "\n":
            i += 2
        elif c == "#":
            while i < len(s) and s[i] != "\n":
                i += 1
        elif c == "\n":
            toks.append(Tok("nl", "\n"))
            i += 1
        elif c.isdigit() or (c == "." and i + 1 < len(s) and s[i + 1].isdigit()):
            m = re.match(r"0[xX][0-9a-fA-F]+|\d*\.?\d*([eE][-+]?\d+)?", s[i:])
            txt = m.group(0)
            toks.append(Tok("num", txt, float(int(txt, 16)) if txt.lower().startswith("0x") else float(txt)))
            i += len(txt)
        elif c == '"':
            out = []
            i += 1
            while i < len(s) and s[i] != '"':
                if s[i] == "\\" and i + 1 < len(s):
                    i += 1
                    out.append(_STR_ESC.get(s[i], "\\" + s[i]))
                else:
                    out.append(s[i])
                i += 1
            if i >= len(s):
                raise AwkError("syntax error: non-terminated string")
            i += 1
            toks.append(Tok("str", "".join(out)))
        elif c == "/" and regex_allowed():
            out, in_class = [], False
            i += 1
            while i < len(s) and (s[i] != "/" or in_class):
                if s[i] == "\n":
                    raise AwkError("syntax error: newline in regex")
                if s[i] == "\\" and i + 1 < len(s):
                    out.append("/" if s[i + 1] == "/" else s[i] + s[i + 1])
                    i += 2
                    continue
                if s[i] == "[":
                    in_class = True
                elif s[i] == "]":
                    in_class = False
                out.append(s[i])
                i += 1
            if i >= len(s):
                raise AwkError("syntax error: non-terminated regular expression")
            i += 1
            toks.append(Tok("regex", "".join(out)))
        elif c.isalpha() or c == "_":
            j = i
            while j < len(s) and (s[j].isalnum() or s[j] == "_"):
                j += 1
            w = s[i:j]
            kind = "kw" if w in _KEYWORDS else "func" if w in _FUNCS else "name"
            if kind == "name" and j < len(s) and s[j] == "(":
                kind = "call"  # a user-defined function call: no space allowed before the (
            toks.append(Tok(kind, w))
            i = j
        else:
            op = next((o for o in _OPS if s.startswith(o, i)), None)
            if op is None:
                raise AwkError(f"syntax error: unexpected character '{c}'")
            toks.append(Tok("op", op))
            i += len(op)
    toks.append(Tok("eof", ""))
    return toks


def _is_lvalue(n) -> bool:
    return isinstance(n, (Var, Field, Index))


class _Parser:
    def __init__(self, src: str):
        self.t = _lex(src)
        self.p = 0
        self.functions: dict[str, tuple[list[str], Block]] = {}

    @staticmethod
    def err(msg: str) -> AwkError:
        return AwkError(f"syntax error: {msg}")

    @property
    def peek(self) -> Tok:
        return self.t[self.p]

    def at(self, o: int) -> Tok:
        return self.t[min(self.p + o, len(self.t) - 1)]

    def is_op(self, o: str) -> bool:
        return self.peek.kind == "op" and self.peek.text == o

    def is_kw(self, k: str) -> bool:
        return self.peek.kind == "kw" and self.peek.text == k

    def accept(self, o: str) -> bool:
        if self.is_op(o):
            self.p += 1
            return True
        return False

    def expect(self, o: str) -> None:
        if not self.accept(o):
            raise self.err(f"expected '{o}' near '{self.peek.text}'")

    def skip_nl(self) -> None:
        while self.peek.kind == "nl":
            self.p += 1

    def skip_terms(self) -> None:
        while self.peek.kind == "nl" or self.is_op(";"):
            self.p += 1

    def at_stmt_end(self) -> bool:
        return self.peek.kind in ("nl", "eof") or self.is_op(";") or self.is_op("}")

    def parse(self) -> list[Rule]:
        rules = []
        self.skip_terms()
        while self.peek.kind != "eof":
            r = Rule()
            if self.is_kw("BEGIN") or self.is_kw("END"):
                r.kind = self.peek.text
                self.p += 1
                self.skip_nl()
                r.action = self.block()
            elif self.is_kw("function") or self.is_kw("func"):
                self.p += 1
                if self.peek.kind not in ("name", "call"):
                    raise self.err("function name expected")
                name = self.peek.text
                self.p += 1
                self.expect("(")
                params = []
                while not self.is_op(")"):
                    if self.peek.kind != "name":
                        raise self.err("function parameter name expected")
                    params.append(self.peek.text)
                    self.p += 1
                    if not self.accept(","):
                        break
                    self.skip_nl()
                self.expect(")")
                self.skip_nl()
                self.functions[name] = (params, self.block())
                self.skip_terms()
                continue
            else:
                if not self.is_op("{"):
                    r.pattern = self.expr(False)
                    if self.accept(","):
                        self.skip_nl()
                        r.pattern2 = self.expr(False)
                if self.is_op("{"):
                    r.action = self.block()
            rules.append(r)
            self.skip_terms()
        return rules

    def block(self) -> Block:
        self.expect("{")
        body = []
        while True:
            self.skip_terms()
            if self.accept("}"):
                return Block(body)
            if self.peek.kind == "eof":
                raise self.err("unexpected end of program, missing '}'")
            body.append(self.statement())

    def end_simple(self) -> None:
        if self.peek.kind == "nl" or self.is_op(";"):
            self.p += 1
        elif not (self.is_op("}") or self.peek.kind == "eof"):
            raise self.err(f"unexpected '{self.peek.text}'")

    def statement(self):
        if self.is_op("{"):
            return self.block()
        if self.accept(";"):
            return Block([])
        if self.peek.kind == "kw":
            k = self.peek.text
            if k == "if":
                self.p += 1
                self.expect("(")
                c = self.expr(False)
                self.expect(")")
                self.skip_nl()
                then = self.statement()
                save = self.p
                self.skip_terms()
                if self.is_kw("else"):
                    self.p += 1
                    self.skip_nl()
                    return IfS(c, then, self.statement())
                self.p = save
                return IfS(c, then, None)
            if k == "while":
                self.p += 1
                self.expect("(")
                c = self.expr(False)
                self.expect(")")
                if self.accept(";"):
                    return WhileS(c, Block([]), False)
                self.skip_nl()
                return WhileS(c, self.statement(), False)
            if k == "do":
                self.p += 1
                self.skip_nl()
                body = self.statement()
                self.skip_terms()
                if not self.is_kw("while"):
                    raise self.err("expected 'while' after 'do' body")
                self.p += 1
                self.expect("(")
                c = self.expr(False)
                self.expect(")")
                self.end_simple()
                return WhileS(c, body, True)
            if k == "for":
                self.p += 1
                self.expect("(")
                if (self.peek.kind == "name" and self.at(1).kind == "kw" and self.at(1).text == "in"
                        and self.at(2).kind == "name" and self.at(3).kind == "op" and self.at(3).text == ")"):
                    var, arr = self.peek.text, self.at(2).text
                    self.p += 4
                    self.skip_nl()
                    return ForInS(var, arr, self.statement())
                init = None if self.is_op(";") else ExprS(self.expr(False))
                self.expect(";")
                self.skip_nl()
                c = None if self.is_op(";") else self.expr(False)
                self.expect(";")
                self.skip_nl()
                step = None if self.is_op(")") else ExprS(self.expr(False))
                self.expect(")")
                if self.accept(";"):
                    return ForS(init, c, step, Block([]))
                self.skip_nl()
                return ForS(init, c, step, self.statement())
            if k in ("next", "nextfile", "break", "continue"):
                self.p += 1
                self.end_simple()
                return SimpleS("next" if k == "nextfile" else k)
            if k == "exit":
                self.p += 1
                arg = None if self.at_stmt_end() else self.expr(False)
                self.end_simple()
                return SimpleS("exit", arg)
            if k == "delete":
                self.p += 1
                if self.peek.kind != "name":
                    raise self.err("delete: expected array name")
                name = self.peek.text
                self.p += 1
                keys = None
                if self.accept("["):
                    keys = self.expr_list(False)
                    self.expect("]")
                self.end_simple()
                return DeleteS(name, keys)
            if k in ("print", "printf"):
                self.p += 1
                args = []
                if self.is_op("("):
                    save = self.p
                    self.p += 1
                    lst = self.expr_list(False)
                    if self.accept(")") and (self.at_stmt_end() or self.is_op(">") or self.is_op(">>") or self.is_op("|")):
                        args = lst
                    else:
                        self.p = save
                        args = self.expr_list(True)
                elif not (self.at_stmt_end() or self.is_op(">") or self.is_op(">>") or self.is_op("|")):
                    args = self.expr_list(True)
                redir = dest = None
                if self.is_op(">") or self.is_op(">>"):
                    redir = self.peek.text
                    self.p += 1
                    dest = self.concat(True)
                elif self.is_op("|"):
                    raise AwkError('output pipes (print | "cmd") are not supported')
                self.end_simple()
                return PrintS(k == "printf", args, redir, dest)
            if k == "return":
                self.p += 1
                value = None if self.at_stmt_end() else self.expr(False)
                self.end_simple()
                return ReturnS(value)
        e = self.expr(False)
        self.end_simple()
        return ExprS(e)

    def expr_list(self, no_gt: bool) -> list:
        lst = [self.expr(no_gt)]
        while self.accept(","):
            self.skip_nl()
            lst.append(self.expr(no_gt))
        return lst

    def expr(self, no_gt: bool):
        lhs = self.ternary(no_gt)
        if self.peek.kind == "op" and self.peek.text in _ASSIGN_OPS and _is_lvalue(lhs):
            op = self.peek.text
            self.p += 1
            self.skip_nl()
            return Assign(lhs, op, self.expr(no_gt))
        return lhs

    def ternary(self, no_gt: bool):
        c = self.or_(no_gt)
        if not self.accept("?"):
            return c
        self.skip_nl()
        a = self.expr(no_gt)
        self.skip_nl()
        self.expect(":")
        self.skip_nl()
        return Cond(c, a, self.expr(no_gt))

    def or_(self, no_gt: bool):
        left = self.and_(no_gt)
        while self.accept("||"):
            self.skip_nl()
            left = Bin("||", left, self.and_(no_gt))
        return left

    def and_(self, no_gt: bool):
        left = self.in_(no_gt)
        while self.accept("&&"):
            self.skip_nl()
            left = Bin("&&", left, self.in_(no_gt))
        return left

    def in_(self, no_gt: bool):
        left = self.match(no_gt)
        while self.is_kw("in"):
            self.p += 1
            if self.peek.kind != "name":
                raise self.err("expected array name after 'in'")
            left = In([left], self.peek.text)
            self.p += 1
        return left

    def match(self, no_gt: bool):
        left = self.comparison(no_gt)
        while self.is_op("~") or self.is_op("!~"):
            neg = self.peek.text == "!~"
            self.p += 1
            left = Match(left, self.comparison(no_gt), neg)
        return left

    def comparison(self, no_gt: bool):
        left = self.concat(no_gt)
        while self.is_op("|") and self.at(1).kind == "kw" and self.at(1).text == "getline":
            self.p += 2
            left = Getline(self.getline_target(), None, left)
        if (self.peek.kind == "op" and self.peek.text in ("<", "<=", "==", "!=", ">=")) or (self.is_op(">") and not no_gt):
            op = self.peek.text
            self.p += 1
            return Bin(op, left, self.concat(no_gt))
        return left

    def starts_concat(self) -> bool:
        return self.peek.kind in ("num", "str", "name", "func", "regex", "call") or self.is_op("$") or self.is_op("(")

    def concat(self, no_gt: bool):
        left = self.additive()
        while self.starts_concat():
            left = Concat(left, self.additive())
        return left

    def additive(self):
        left = self.multiplicative()
        while self.is_op("+") or self.is_op("-"):
            op = self.peek.text
            self.p += 1
            left = Bin(op, left, self.multiplicative())
        return left

    def multiplicative(self):
        left = self.unary()
        while self.is_op("*") or self.is_op("/") or self.is_op("%"):
            op = self.peek.text
            self.p += 1
            left = Bin(op, left, self.unary())
        return left

    def unary(self):
        if self.is_op("!") or self.is_op("-") or self.is_op("+"):
            op = self.peek.text
            self.p += 1
            return Un(op, self.unary())
        return self.power()

    def power(self):
        b = self.postfix()
        if self.is_op("^") or self.is_op("**"):
            self.p += 1
            return Bin("^", b, self.unary())
        return b

    def postfix(self):
        e = self.prefix()
        if _is_lvalue(e) and (self.is_op("++") or self.is_op("--")):
            inc = self.peek.text == "++"
            self.p += 1
            return Incr(e, inc, False)
        return e

    def prefix(self):
        if self.is_op("++") or self.is_op("--"):
            inc = self.peek.text == "++"
            self.p += 1
            t = self.prefix()
            if not _is_lvalue(t):
                raise self.err("++ or -- needs a variable")
            return Incr(t, inc, True)
        return self.primary()

    def getline_target(self):
        """The optional variable after getline."""
        if self.peek.kind == "name" or self.is_op("$"):
            target = self.primary()
            if _is_lvalue(target):
                return target
            raise self.err("getline: expected a variable")
        return None

    def primary(self):
        t = self.peek
        if t.kind == "num":
            self.p += 1
            return Num(t.num)
        if t.kind == "str":
            self.p += 1
            return Str(t.text)
        if t.kind == "regex":
            self.p += 1
            return Rx(t.text)
        if t.kind == "op" and t.text == "$":
            self.p += 1
            return Field(self.prefix() if self.is_op("++") or self.is_op("--") else self.primary())
        if t.kind == "op" and t.text == "(":
            self.p += 1
            lst = self.expr_list(False)
            self.expect(")")
            if len(lst) == 1:
                return lst[0]
            if not self.is_kw("in"):
                raise self.err("expected 'in' after (a, b)")
            self.p += 1
            if self.peek.kind != "name":
                raise self.err("expected array name")
            name = self.peek.text
            self.p += 1
            return In(lst, name)
        if t.kind == "op" and t.text in ("-", "+", "!"):
            return self.unary()
        if t.kind == "func":
            self.p += 1
            if not self.accept("("):
                if t.text == "length":
                    return Call("length", [])
                raise self.err(f"expected '(' after {t.text}")
            args = [] if self.is_op(")") else self.expr_list(False)
            self.expect(")")
            return Call(t.text, args)
        if t.kind == "name":
            self.p += 1
            if self.accept("["):
                keys = self.expr_list(False)
                self.expect("]")
                return Index(t.text, keys)
            return Var(t.text)
        if t.kind == "kw" and t.text == "getline":
            self.p += 1
            target = self.getline_target()
            file = None
            if self.accept("<"):
                file = self.primary()
            return Getline(target, file)
        if t.kind == "call":
            self.p += 1
            self.expect("(")
            args = [] if self.is_op(")") else self.expr_list(False)
            self.expect(")")
            return UserCall(t.text, args)
        raise self.err("unexpected end of program" if t.kind == "eof" else f"unexpected '{t.text}'")


# ---------- interpreter ----------

class _Next(Exception):
    pass


class _Exit(Exception):
    pass


class _Break(Exception):
    pass


class _Continue(Exception):
    pass


class _Return(Exception):
    def __init__(self, value):
        super().__init__()
        self.value = value


class _Frame:
    """A user function call: parameters are locals; array arguments are passed by reference."""

    def __init__(self, params: list[str]):
        self.params = set(params)
        self.scalars: dict = {}
        self.arrays: dict[str, dict] = {}
        self.pending: dict[str, str] = {}  # parameter -> unset global variable passed for it


class _Runner:
    def __init__(self, ctx):
        self.ctx = ctx
        self.vars: dict = {}
        self.arrays: dict[str, dict] = {}
        self.regexes: dict[str, re.Pattern] = {}
        self.outputs: dict[str, list] = {}  # path -> [append, chunks]
        self.fields: list[str] = [""]
        self.exit_code = 0
        self.iterations = 0
        self.rng = random.Random(0)
        self.functions: dict[str, tuple[list[str], Block]] = {}
        self.frames: list[_Frame] = []
        self.input = iter(())  # the main input's records, shared by the rules and getline
        self.getline_files: dict[str, object] = {}

    # variables & fields
    def local(self, name: str):
        """The current function frame when name is one of its parameters, else None."""
        return self.frames[-1] if self.frames and name in self.frames[-1].params else None

    def is_array(self, name: str) -> bool:
        f = self.local(name)
        return name in f.arrays if f else name in self.arrays

    def get_var(self, name: str):
        f = self.local(name)
        if f:
            if name in f.arrays:
                raise AwkError(f"attempt to use array {name} in a scalar context")
            return f.scalars.get(name)
        if name == "NF":
            return float(len(self.fields) - 1)
        return self.vars.get(name)

    def set_var(self, name: str, v) -> None:
        f = self.local(name)
        if f:
            if name in f.arrays:
                raise AwkError(f"can't assign to {name}; it's an array name.")
            f.scalars[name] = v
            return
        if name == "NF":
            n = max(0, int(to_num(v)))
            self.fields = (self.fields + [""] * n)[: n + 1]
            self.rebuild()
            return
        if name in self.arrays:
            raise AwkError(f"can't assign to {name}; it's an array name.")
        self.vars[name] = v

    def split_fields(self, s: str, fs: str) -> list[str]:
        if not s:
            return []
        if fs == " ":
            return s.split()
        if len(fs) == 1 and fs != "\\":
            return s.split(fs)
        if not fs:
            return list(s)
        return self.regex(fs).split(s)

    def set_record(self, line: str) -> None:
        fs = to_str(self.vars.get("FS"))
        if self.vars.get("RS") == "" and fs != " ":  # paragraph mode: newline always separates fields too
            fs = "\n" if not fs else f"(?:{fs if len(fs) > 1 else re.escape(fs)})|\n"
        self.fields = [line] + self.split_fields(line, fs)

    def records(self, text: str):
        """Splits input into records on RS: newline, one character, a regex, or "" (blank-line separated paragraphs)."""
        rs = to_str(self.vars.get("RS"))
        if rs == "\n":
            return split_lines(text)
        if rs == "":
            parts = re.split(r"\n\n+", text.strip("\n"))
            return [p for p in parts if p]
        parts = text.split(rs) if len(rs) == 1 else self.regex(rs).split(text)
        if parts and parts[-1] == "":
            parts.pop()
        return parts

    def str(self, v) -> str:
        """Number to string with CONVFMT (integers stay integers)."""
        if isinstance(v, float):
            return fmt_number(v, to_str(self.vars.get("CONVFMT")) or "%.6g")
        return to_str(v)

    def rebuild(self) -> None:
        self.fields[0] = to_str(self.get_var("OFS")).join(self.fields[1:])

    def get_field(self, i: int) -> str:
        if i < 0:
            raise AwkError(f"trying to access out of range field {i}")
        return self.fields[i] if i < len(self.fields) else ""

    def set_field(self, i: int, v: str) -> None:
        if i < 0:
            raise AwkError(f"trying to access out of range field {i}")
        if i == 0:
            self.set_record(v)
            return
        while len(self.fields) <= i:
            self.fields.append("")
        self.fields[i] = v
        self.rebuild()

    def array(self, name: str) -> dict:
        f = self.local(name)
        if f:
            if f.scalars.get(name) is not None:
                raise AwkError(f"can't use scalar {name} as array")
            if name in f.pending:
                f.arrays[name] = self.arrays.setdefault(f.pending.pop(name), {})
            return f.arrays.setdefault(name, {})
        if self.vars.get(name) is not None:
            raise AwkError(f"can't use scalar {name} as array")
        return self.arrays.setdefault(name, {})

    def key(self, keys: list) -> str:
        return to_str(self.get_var("SUBSEP")).join(self.str(self.eval(k)) for k in keys)

    def regex(self, pattern: str) -> re.Pattern:
        rx = self.regexes.get(pattern)
        if rx is None:
            from .text import posix_to_python

            try:
                rx = self.regexes[pattern] = re.compile(posix_to_python(pattern, True, bracket_escapes=True))
            except re.error as e:
                raise AwkError(f"invalid regular expression /{pattern}/: {e}") from None
        return rx

    def regex_of(self, n) -> re.Pattern:
        return self.regex(n.pattern if isinstance(n, Rx) else to_str(self.eval(n)))

    def get(self, lv):
        if isinstance(lv, Var):
            return self.get_var(lv.name)
        if isinstance(lv, Field):
            return StrNum(self.get_field(int(to_num(self.eval(lv.index)))))
        arr = self.array(lv.name)
        return arr.setdefault(self.key(lv.keys), None)

    def set(self, lv, v) -> None:
        if isinstance(lv, Var):
            self.set_var(lv.name, v)
        elif isinstance(lv, Field):
            self.set_field(int(to_num(self.eval(lv.index))), to_str(v))
        else:
            self.array(lv.name)[self.key(lv.keys)] = v

    # statements
    def exec(self, s) -> None:
        if isinstance(s, Block):
            for x in s.body:
                self.exec(x)
        elif isinstance(s, ExprS):
            self.eval(s.e)
        elif isinstance(s, PrintS):
            self.print(s)
        elif isinstance(s, IfS):
            if truthy(self.eval(s.c)):
                self.exec(s.then)
            elif s.else_ is not None:
                self.exec(s.else_)
        elif isinstance(s, WhileS):
            if s.do_while:
                while self.loop(s.body) and truthy(self.eval(s.c)):
                    pass
            else:
                while truthy(self.eval(s.c)) and self.loop(s.body):
                    pass
        elif isinstance(s, ForS):
            if s.init is not None:
                self.exec(s.init)
            while s.c is None or truthy(self.eval(s.c)):
                if not self.loop(s.body):
                    break
                if s.step is not None:
                    self.exec(s.step)
        elif isinstance(s, ForInS):
            for k in list(self.array(s.array)):
                self.set_var(s.var, StrNum(k))
                if not self.loop(s.body):
                    break
        elif isinstance(s, SimpleS):
            if s.kind == "next":
                raise _Next()
            if s.kind == "exit":
                if s.arg is not None:
                    self.exit_code = int(to_num(self.eval(s.arg)))
                raise _Exit()
            raise _Break() if s.kind == "break" else _Continue()
        elif isinstance(s, ReturnS):
            if not self.frames:
                raise AwkError("return outside function")
            raise _Return(None if s.value is None else self.eval(s.value))
        elif isinstance(s, DeleteS):
            if s.keys is None:
                self.array(s.array).clear()
            else:
                self.array(s.array).pop(self.key(s.keys), None)

    def too_large(self):
        return self.ctx.shell.too_large("awk: string", "build smaller strings or print as you go")

    def loop(self, body) -> bool:
        """Runs a loop body; False means `break`."""
        self.iterations += 1
        if self.iterations > 10_000_000:
            raise AwkError("loop iteration limit exceeded")
        if self.iterations & 1023 == 0:
            self.ctx.check_time()  # the call's time limit, or `timeout`
        try:
            self.exec(body)
            return True
        except _Break:
            return False
        except _Continue:
            return True

    def output_string(self, v) -> str:
        if isinstance(v, float):
            return fmt_number(v, to_str(self.get_var("OFMT")))
        return to_str(v)

    def print(self, p: PrintS) -> None:
        if p.printf:
            if not p.args:
                raise AwkError("printf: no format")
            text = sprintf(to_str(self.eval(p.args[0])), [self.eval(a) for a in p.args[1:]])
        else:
            body = self.fields[0] if not p.args else to_str(self.get_var("OFS")).join(self.output_string(self.eval(a)) for a in p.args)
            text = body + to_str(self.get_var("ORS"))
        if p.dest is None:
            self.ctx.stdout.write(text)
            return
        dest = to_str(self.eval(p.dest))
        if dest in ("/dev/stdout", "-"):
            self.ctx.stdout.write(text)
        elif dest == "/dev/stderr":
            self.ctx.stderr.write(text)
        else:
            self.outputs.setdefault(self.ctx.resolve(dest), [p.redir == ">>", []])[1].append(text)

    # expressions
    def eval(self, n):
        if isinstance(n, Num):
            return n.v
        if isinstance(n, Str):
            return n.v
        if isinstance(n, Rx):
            return float(bool(self.regex(n.pattern).search(self.fields[0])))
        if isinstance(n, (Var, Field, Index)):
            return self.get(n)
        if isinstance(n, Assign):
            v = self.eval(n.value)
            if n.op != "=":
                v = self.arith(n.op[:-1], to_num(self.get(n.target)), to_num(v))
            self.set(n.target, v)
            return v
        if isinstance(n, Cond):
            return self.eval(n.a) if truthy(self.eval(n.c)) else self.eval(n.b)
        if isinstance(n, Bin):
            if n.op == "&&":
                return float(truthy(self.eval(n.l)) and truthy(self.eval(n.r)))
            if n.op == "||":
                return float(truthy(self.eval(n.l)) or truthy(self.eval(n.r)))
            l, r = self.eval(n.l), self.eval(n.r)
            cmp_ops = {"<": lambda c: c < 0, "<=": lambda c: c <= 0, ">": lambda c: c > 0, ">=": lambda c: c >= 0,
                       "==": lambda c: c == 0, "!=": lambda c: c != 0}
            if n.op in cmp_ops:
                return float(cmp_ops[n.op](compare(l, r)))
            return self.arith(n.op, to_num(l), to_num(r))
        if isinstance(n, Un):
            v = self.eval(n.e)
            return float(not truthy(v)) if n.op == "!" else -to_num(v) if n.op == "-" else to_num(v)
        if isinstance(n, Incr):
            old = to_num(self.get(n.target))
            nv = old + (1 if n.inc else -1)
            self.set(n.target, nv)
            return nv if n.prefix else old
        if isinstance(n, Match):
            return float(bool(self.regex_of(n.rx).search(to_str(self.eval(n.s)))) != n.negate)
        if isinstance(n, In):
            return float(self.key(n.keys) in self.array(n.array))
        if isinstance(n, Concat):
            left, right = self.str(self.eval(n.l)), self.str(self.eval(n.r))
            if len(left) + len(right) > self.ctx.shell.max_string_bytes:
                raise self.too_large()
            return left + right
        if isinstance(n, Call):
            return self.call(n)
        if isinstance(n, UserCall):
            return self.user_call(n)
        if isinstance(n, Getline):
            return self.getline(n)
        raise AwkError("unsupported expression")

    @staticmethod
    def arith(op: str, l: float, r: float) -> float:
        if op in ("/", "%") and r == 0:
            raise AwkError("division by zero" + (" in %" if op == "%" else ""))
        if op == "+":
            return l + r
        if op == "-":
            return l - r
        if op == "*":
            return l * r
        if op == "/":
            return l / r
        if op == "%":
            return math.fmod(l, r)
        if op in ("^", "**"):
            return float(l ** r)
        raise AwkError(f"unknown operator {op}")

    def call(self, c: Call):
        a = c.args

        def s(i):
            return self.str(self.eval(a[i])) if i < len(a) else ""

        def num(i):
            return to_num(self.eval(a[i])) if i < len(a) else 0.0

        name = c.name
        if name == "length":
            if not a:
                return float(len(self.fields[0]))
            if isinstance(a[0], Var) and self.is_array(a[0].name):
                return float(len(self.array(a[0].name)))
            return float(len(s(0)))
        if name == "substr":
            text = s(0)
            m = round(num(1))
            end = m + round(num(2)) if len(a) > 2 else math.inf
            start = max(m, 1)
            end = min(end, len(text) + 1)
            return "" if end <= start else text[int(start) - 1:int(end) - 1]
        if name == "index":
            return float(s(0).find(s(1)) + 1)
        if name == "split":
            if len(a) < 2 or not isinstance(a[1], Var):
                raise AwkError("split: second argument must be an array name")
            src = s(0)
            if len(a) > 2:
                parts = self.regex(a[2].pattern).split(src) if isinstance(a[2], Rx) else self.split_fields(src, s(2))
            else:
                parts = self.split_fields(src, to_str(self.get_var("FS")))
            if not src:
                parts = []
            dest = self.array(a[1].name)
            dest.clear()
            for i, part in enumerate(parts):
                dest[str(i + 1)] = StrNum(part)
            return float(len(parts))
        if name in ("sub", "gsub"):
            if len(a) < 2:
                raise AwkError(f"{name}: requires at least 2 arguments")
            rx = self.regex_of(a[0])
            repl = s(1)
            target = a[2] if len(a) > 2 else Field(Num(0.0))
            src = to_str(self.get(target))

            def rep(m: re.Match) -> str:
                out, i = [], 0
                while i < len(repl):
                    if repl[i] == "\\" and i + 1 < len(repl) and repl[i + 1] in "&\\":
                        out.append(repl[i + 1])
                        i += 2
                        continue
                    out.append(m.group(0) if repl[i] == "&" else repl[i])
                    i += 1
                return "".join(out)

            result, count = rx.subn(rep, src, count=1 if name == "sub" else 0)
            if len(result) > self.ctx.shell.max_string_bytes:
                raise self.too_large()
            if count:
                self.set(target, result)
            return float(count)
        if name == "match":
            m = self.regex_of(a[1]).search(s(0))
            self.set_var("RSTART", float(m.start() + 1 if m else 0))
            self.set_var("RLENGTH", float(len(m.group(0)) if m else -1))
            return float(m.start() + 1 if m else 0)
        if name == "sprintf":
            return sprintf(s(0), [self.eval(x) for x in a[1:]])
        simple = {
            "tolower": lambda: s(0).lower(), "toupper": lambda: s(0).upper(), "int": lambda: float(math.trunc(num(0))),
            "sqrt": lambda: math.sqrt(num(0)), "exp": lambda: math.exp(num(0)), "log": lambda: math.log(num(0)),
            "sin": lambda: math.sin(num(0)), "cos": lambda: math.cos(num(0)), "atan2": lambda: math.atan2(num(0), num(1)),
            "rand": lambda: self.rng.random(), "srand": lambda: 0.0, "fflush": lambda: 0.0,
        }
        if name == "close":
            return 0.0 if self.getline_files.pop(self.ctx.resolve(s(0)), None) is not None or s(0) in self.outputs else -1.0
        if name in simple:
            return simple[name]()
        if name == "system":
            raise AwkError("system() is not supported")
        raise AwkError(f"calling undefined function {name}")

    def user_call(self, c: UserCall):
        if c.name not in self.functions:
            raise AwkError(f"function `{c.name}' not defined")
        params, body = self.functions[c.name]
        if len(c.args) > len(params):
            raise AwkError(f"function `{c.name}' called with more arguments than declared")
        if len(self.frames) >= 200:
            raise AwkError(f"function `{c.name}': call nesting too deep")
        frame = _Frame(params)
        for param, arg in zip(params, c.args):
            if isinstance(arg, Var) and self.is_array(arg.name):
                frame.arrays[param] = self.array(arg.name)  # arrays are passed by reference
            elif isinstance(arg, Var) and self.local(arg.name) is None and arg.name not in self.vars and arg.name != "NF":
                frame.pending[param] = arg.name  # an unset global becomes a global array if the function uses it as one
            else:
                frame.scalars[param] = self.eval(arg)
        self.frames.append(frame)
        try:
            self.exec(body)
            return None
        except _Return as r:
            return r.value
        finally:
            self.frames.pop()

    def getline(self, g: Getline):
        if g.cmd is not None:
            raise AwkError('"command" | getline is not supported')
        if g.file is not None:
            name = to_str(self.eval(g.file))
            if name in ("-", "/dev/stdin"):
                it = self.getline_files.setdefault("-", iter(self.records(self.ctx.stdin)))
            else:
                path = self.ctx.resolve(name)
                it = self.getline_files.get(path)
                if it is None:
                    if not self.ctx.fs.is_file(path):
                        return -1.0
                    it = self.getline_files[path] = iter(self.records(self.ctx.fs.read_text(path)))
            rec = next(it, None)
            if rec is None:
                return 0.0
        else:
            rec = next(self.input, None)
            if rec is None:
                return 0.0
            self.vars["NR"] = to_num(self.vars["NR"]) + 1
            self.vars["FNR"] = to_num(self.vars["FNR"]) + 1
        if g.target is None:
            self.set_record(rec)
        else:
            self.set(g.target, StrNum(rec))
        return 1.0

    # driver
    def assign(self, text: str) -> bool:
        m = re.fullmatch(r"([A-Za-z_][A-Za-z0-9_]*)=(.*)", text, re.S)
        if not m:
            return False
        self.set_var(m.group(1), StrNum(unescape(m.group(2))))
        return True

    def matches(self, r: Rule) -> bool:
        if r.pattern is None:
            return True
        if r.pattern2 is None:
            return truthy(self.eval(r.pattern))
        if r.in_range:
            if truthy(self.eval(r.pattern2)):
                r.in_range = False
            return True
        if not truthy(self.eval(r.pattern)):
            return False
        r.in_range = not truthy(self.eval(r.pattern2))
        return True

    def run(self) -> int:
        ctx = self.ctx
        args = ctx.args
        program = None
        assigns, fs = [], None
        i = 0
        while i < len(args):
            a = args[i]
            if a == "--":
                i += 1
                break
            if a == "--version" or (a == "-W" and i + 1 < len(args) and args[i + 1] == "version"):
                ctx.print("GNU Awk 5.2.1, API 3.2, PMA Avon 8-g1, (GNU MPFR 4.2.1, GNU MP 6.3.0)")
                return 0
            if len(a) < 2 or a[0] != "-":
                break

            def val(flag):
                nonlocal i
                if len(a) > 2:
                    return a[2:]
                i += 1
                if i >= len(args):
                    raise AwkError(f"option requires an argument -- {flag}")
                return args[i]

            if a.startswith("-F"):
                fs = val("F")
            elif a.startswith("-v"):
                assigns.append(val("v"))
            elif a.startswith("-f"):
                source = val("f")
                program = ctx.stdin if source in ("-", "/dev/stdin") else ctx.fs.read_text(ctx.resolve(source))
            else:
                raise AwkError(f"unknown option {a}")
            i += 1
        if program is None:
            if i >= len(args):
                raise AwkError("usage: awk [-F fs][-v var=value][prog | -f progfile][file ...]")
            program = args[i]
            i += 1
        operands = args[i:]

        parser = _Parser(program)
        rules = parser.parse()
        self.functions = parser.functions
        defaults = {"FS": " " if fs is None else "\t" if fs == "t" else unescape(fs), "OFS": " ", "ORS": "\n", "RS": "\n",
                    "SUBSEP": "\x1c", "CONVFMT": "%.6g", "OFMT": "%.6g", "FILENAME": ""}
        for k, v in defaults.items():
            self.vars[k] = v
        for k in ("NR", "FNR", "RSTART"):
            self.vars[k] = 0.0
        self.vars["RLENGTH"] = -1.0
        self.arrays["ENVIRON"] = {k: StrNum(v) for k, v in ctx.shell.env.items()}
        for a in assigns:
            self.assign(a)

        self.input = self.input_records(operands)
        try:
            for r in rules:
                if r.kind == "BEGIN":
                    self.exec(r.action)
            if any(r.kind != "BEGIN" for r in rules):
                self.process(rules)
        except _Exit:
            pass
        try:
            for r in rules:
                if r.kind == "END":
                    self.exec(r.action)
        except _Exit:
            pass

        for path, (append, chunks) in self.outputs.items():
            (ctx.fs.append_text if append else ctx.fs.write_text)(path, "".join(chunks))
        return self.exit_code

    def input_records(self, operands: list[str]):
        """The main input: records of each file operand in turn (var=value operands are applied when reached), or stdin."""
        ctx = self.ctx
        read_any = False
        for op in operands:
            if self.assign(op):
                continue
            read_any = True
            if op == "-":
                name, text = "", ctx.stdin
            else:
                path = ctx.resolve(op)
                if not ctx.fs.is_file(path):
                    reason = "Is a directory" if ctx.fs.is_dir(path) else "No such file or directory"
                    ctx.error(f'awk: cannot open "{op}" ({reason})')
                    self.exit_code = 2
                    continue
                name, text = op, ctx.fs.read_text(path)
            self.vars["FILENAME"] = name
            self.vars["FNR"] = 0.0
            yield from self.records(text)
        if not read_any:
            self.vars["FILENAME"] = ""
            yield from self.records(ctx.stdin)

    def process(self, rules: list[Rule]) -> None:
        main = [r for r in rules if r.kind == "main"]
        while True:
            line = next(self.input, None)
            if line is None:
                return
            self.ctx.check_time()  # slow patterns over many records: the call's time limit
            self.vars["NR"] = to_num(self.vars["NR"]) + 1
            self.vars["FNR"] = to_num(self.vars["FNR"]) + 1
            self.set_record(line)
            try:
                for r in main:
                    if self.matches(r):
                        if r.action is not None:
                            self.exec(r.action)
                        else:
                            self.print(PrintS(False, [], None, None))
            except _Next:
                pass


def register(shell) -> None:
    shell.register_command("awk", lambda ctx: _Runner(ctx).run(), "Pattern scanning and processing language")
