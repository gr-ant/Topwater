"""Clean-room lexer and parser for a practical subset of POSIX sh / bash syntax."""
from __future__ import annotations

import re
from dataclasses import dataclass, field


class ParseError(Exception):
    pass


@dataclass(frozen=True)
class Literal:
    text: str
    quoted: bool


@dataclass(frozen=True)
class Variable:
    """$name or ${expr} (expr may contain operators like :- or %%)."""
    name: str
    quoted: bool


@dataclass(frozen=True)
class CommandSub:
    source: str
    quoted: bool


@dataclass(frozen=True)
class ArithSub:
    expr: str
    quoted: bool


@dataclass(frozen=True)
class ProcSub:
    """<(cmd): replaced by the path of a file holding cmd's output."""
    source: str
    quoted: bool = True


@dataclass(frozen=True)
class ExpandedText:
    """Unquoted text that came out of an expansion (the word of ${x:-a b}): it is split and globbed like one."""
    text: str
    quoted: bool = False


@dataclass(frozen=True)
class ArrayLiteral:
    """The (a b c) of name=(a b c): its source text, expanded when the assignment runs."""
    source: str
    quoted: bool = False


@dataclass
class Word:
    parts: list

    @staticmethod
    def lit(text: str) -> "Word":
        return Word([Literal(text, False)])

    @property
    def unquoted_literal(self) -> str | None:
        if len(self.parts) == 1 and isinstance(self.parts[0], Literal) and not self.parts[0].quoted:
            return self.parts[0].text
        return None

    def is_(self, text: str) -> bool:
        return self.unquoted_literal == text

    def __str__(self) -> str:
        out = []
        for p in self.parts:
            if isinstance(p, Literal):
                out.append(p.text)
            elif isinstance(p, Variable):
                out.append("${" + p.name + "}")
            elif isinstance(p, ArithSub):
                out.append("$((" + p.expr + "))")
            elif isinstance(p, ProcSub):
                out.append("<(" + p.source + ")")
            elif isinstance(p, ArrayLiteral):
                out.append("(" + p.source + ")")
            elif isinstance(p, ExpandedText):
                out.append(p.text)
            else:
                out.append("$(" + p.source + ")")
        return "".join(out)


@dataclass
class HereDoc:
    delimiter: str
    expand: bool
    strip_tabs: bool
    body: Word = field(default_factory=lambda: Word([Literal("", True)]))


@dataclass
class Redirect:
    fd: int
    op: str
    target: Word | None = None
    here: HereDoc | None = None
    fd_variable: str | None = None  # {name}>file: the shell picks the fd and stores it in this variable


@dataclass
class Assignment:
    """name=value, name[index]=value or name=(elements); append is += on an array or an element."""
    name: str
    value: Word
    index: Word | None = None
    elements: str | None = None
    append: bool = False


# ---------- AST: every command has .redirects ----------

@dataclass
class SimpleCommand:
    assignments: list  # list[Assignment]
    words: list
    redirects: list = field(default_factory=list)
    line: int = 0


@dataclass
class GroupCommand:
    body: "Script"
    subshell: bool
    redirects: list = field(default_factory=list)


@dataclass
class IfCommand:
    branches: list  # list[tuple[Script, Script]]
    else_body: "Script | None"
    redirects: list = field(default_factory=list)


@dataclass
class LoopCommand:
    cond: "Script"
    body: "Script"
    until: bool
    redirects: list = field(default_factory=list)


@dataclass
class ForCommand:
    var: str
    items: list | None
    body: "Script"
    redirects: list = field(default_factory=list)
    select: bool = False  # a select menu rather than a for loop


@dataclass
class ArithForCommand:
    init: str
    cond: str
    step: str
    body: "Script"
    redirects: list = field(default_factory=list)


@dataclass
class CaseCommand:
    subject: Word
    clauses: list  # list[tuple[list[Word], Script, str]]; terminator ";;", ";&" (fall through) or ";;&" (keep testing)
    redirects: list = field(default_factory=list)


@dataclass
class FunctionDef:
    name: str
    body: object
    redirects: list = field(default_factory=list)
    source: str = ""  # the body as written, for `declare -f`


@dataclass
class ArithCommand:
    expr: str
    redirects: list = field(default_factory=list)


@dataclass
class CondCommand:
    """[[ ... ]]: operands are words; operators (&& || ! ( ) == ...) are unquoted literal words."""
    items: list
    redirects: list = field(default_factory=list)


@dataclass
class Pipeline:
    commands: list
    negate: bool
    timed: bool = False  # `time [-p] pipeline`: report how long it took on the shell's stderr
    posix_time: bool = False


@dataclass
class AndOr:
    first: Pipeline
    rest: list  # list[tuple[str, Pipeline]]
    background: bool = False  # ended with `&`: runs (to completion, in order) as a background job in a subshell


@dataclass
class Script:
    items: list


@dataclass
class Token:
    kind: str  # "word" | "op" | "redirect" | "arith"
    op: str = ""
    word: Word | None = None
    redirect: Redirect | None = None
    line: int = 0
    start: int = 0
    end: int = 0

    def __str__(self) -> str:
        if self.kind == "word":
            return str(self.word)
        if self.kind == "op":
            return "newline" if self.op == "\n" else self.op
        if self.kind == "arith":
            return "(("
        return self.redirect.op


_PARENS = re.compile(r"[()]")
_WORD_BREAK = set(" \t\r\n|&;()<>")


class Lexer:
    def __init__(self, s: str):
        self.s = s
        self.i = 0
        self.pending: list[HereDoc] = []
        self._line_pos, self._line = 0, 1

    def line_at(self, pos: int) -> int:
        """The 1-based line of position pos; positions only move forward, so this counts incrementally."""
        s = self.s
        while self._line_pos < pos and self._line_pos < len(s):
            if s[self._line_pos] == "\n":
                self._line += 1
            self._line_pos += 1
        return self._line

    def peek(self, o: int = 0) -> str:
        j = self.i + o
        return self.s[j] if j < len(self.s) else "\0"

    def lex(self) -> list[Token]:
        s = self.s
        tokens: list[Token] = []
        while True:
            while self.peek() in " \t\r":
                self.i += 1
            if self.peek() == "\\" and self.peek(1) == "\n":
                self.i += 2
                continue
            if self.i >= len(s):
                break
            count, start = len(tokens), self.i
            self.lex_one(tokens)
            if len(tokens) > count:
                t = tokens[count]
                t.line, t.start, t.end = self.line_at(start), start, self.i
        self.read_heredoc_bodies()  # heredoc on the last line with no trailing newline: body is empty
        return tokens

    @staticmethod
    def command_position(tokens: list[Token]) -> bool:
        """True where a word can be an assignment: at the start of a command or after another assignment."""
        if not tokens or tokens[-1].kind in ("op", "arith"):
            return True
        w = tokens[-1].word
        return w is not None and (_try_assignment(w) is not None or w.unquoted_literal in _CMD_START)

    def lex_one(self, tokens: list[Token]) -> None:
        s = self.s
        c = s[self.i]
        if c == "#":
            while self.i < len(s) and s[self.i] != "\n":
                self.i += 1
            return
        if c == "\n":
            self.i += 1
            tokens.append(Token("op", "\n"))
            self.read_heredoc_bodies()
            return
        if c.isdigit():
            j = self.i
            while j < len(s) and s[j].isdigit():
                j += 1
            if j < len(s) and s[j] in "<>":
                fd = int(s[self.i:j])
                self.i = j
                tokens.append(self.lex_redirect(fd))
                return
        if c == "{":
            brace = s.find("}", self.i)
            if brace > 0 and brace + 1 < len(s) and s[brace + 1] in "<>" and _is_name(s[self.i + 1:brace]):
                name = s[self.i + 1:brace]
                self.i = brace + 1
                t = self.lex_redirect(10)
                t.redirect.fd_variable = name
                tokens.append(t)
                return
        if c == "&" and self.peek(1) == ">":
            self.i += 2
            append = self.peek() == ">"
            if append:
                self.i += 1
            tokens.append(Token("redirect", redirect=Redirect(1, "&>>" if append else "&>")))
            return
        if tokens and tokens[-1].kind == "word" and tokens[-1].word.is_("=~"):
            tokens.append(Token("word", word=self.lex_word(regex=True)))
            return
        if c == "<" and self.peek(1) == "(":
            tokens.append(Token("word", word=self.lex_word()))
            return
        if c == ">" and self.peek(1) == "(":
            raise ParseError("process substitution >(...) is not supported by this shell (write to a file, then read it)")
        if c in "<>":
            tokens.append(self.lex_redirect(1 if c == ">" else 0))
            return
        if c == "(" and self.peek(1) == "(":
            close = self.find_arith_close(self.i + 2)
            if close >= 0:
                tokens.append(Token("arith", s[self.i + 2:close]))
                self.i = close + 2
                return
        if (c == ";" and self.peek(1) == "&") or (c == ";" and self.peek(1) == ";" and self.peek(2) == "&"):  # case fall-through
            op = ";&" if self.peek(1) == "&" else ";;&"
            self.i += len(op)
            tokens.append(Token("op", op))
            return
        if (c == "&" and self.peek(1) == "&") or (c == "|" and self.peek(1) == "|") or (c == ";" and self.peek(1) == ";"):
            self.i += 2
            tokens.append(Token("op", c * 2))
            return
        if c in "|&;()":
            self.i += 1
            tokens.append(Token("op", c))
            return
        tokens.append(Token("word", word=self.lex_word(subscript=self.command_position(tokens))))

    def find_arith_close(self, k: int) -> int:
        """Index of the '))' closing an arithmetic expression whose body starts at k, or -1."""
        s = self.s
        depth = 0
        for m in _PARENS.finditer(s, k):  # only the parentheses matter; the regex skips the rest at C speed
            if m.group() == "(":
                depth += 1
            elif depth:
                depth -= 1
            else:
                j = m.start()
                return j if j + 1 < len(s) and s[j + 1] == ")" else -1
        return -1

    def lex_redirect(self, fd: int) -> Token:
        s = self.s
        if s[self.i] == "<":
            if s.startswith("<<<", self.i):
                op = "<<<"
            elif s.startswith("<<-", self.i):
                op = "<<-"
            elif self.peek(1) == "<":
                op = "<<"
            elif self.peek(1) == "&":
                op = "<&"
            else:
                op = "<"
        elif self.peek(1) == ">":
            op = ">>"
        elif self.peek(1) == "&":
            op = ">&"
        elif self.peek(1) == "|":
            op = ">|"
        else:
            op = ">"
        self.i += len(op)

        if op in ("<<", "<<-"):
            while self.peek() in " \t":
                self.i += 1
            if self.i >= len(s) or s[self.i] in "\n;|&<>()":
                raise ParseError("syntax error near unexpected token `newline'")
            w = self.lex_word()
            doc = HereDoc(
                "".join(p.text for p in w.parts if isinstance(p, Literal)),
                expand=not any(p.quoted for p in w.parts),
                strip_tabs=op == "<<-",
            )
            self.pending.append(doc)
            return Token("redirect", redirect=Redirect(fd, "<<", here=doc))
        return Token("redirect", redirect=Redirect(fd, op))

    def read_heredoc_bodies(self) -> None:
        s = self.s
        for doc in self.pending:
            body = []
            while self.i < len(s):
                end = s.find("\n", self.i)
                line = s[self.i:] if end < 0 else s[self.i:end]
                self.i = len(s) if end < 0 else end + 1
                if doc.strip_tabs:
                    line = line.lstrip("\t")
                if line.rstrip("\r") == doc.delimiter:
                    break
                body.append(line + "\n")
            text = "".join(body)
            doc.body = Word(Lexer(text).lex_quoted(None)) if doc.expand else Word([Literal(text, True)])
        self.pending.clear()

    def subscript_end(self) -> int:
        """At the start of an assignment word name[sub]=, the index of the subscript's closing bracket (bash lets the
        subscript hold spaces there: a[i + 1]=x); otherwise -1."""
        s, k = self.s, self.i
        if k >= len(s) or not (s[k].isalpha() or s[k] == "_"):
            return -1
        while k < len(s) and (s[k].isalnum() or s[k] == "_"):
            k += 1
        if k >= len(s) or s[k] != "[":
            return -1
        depth = 0
        while k < len(s):
            c = s[k]
            if c == "\\":
                k += 1
            elif c == "'":
                k = s.find("'", k + 1)
                if k < 0:
                    return -1
            elif c == '"':
                k += 1
                while k < len(s) and s[k] != '"':
                    if s[k] == "\\":
                        k += 1
                    k += 1
            elif c == "\n":
                return -1
            elif c == "[":
                depth += 1
            elif c == "]":
                depth -= 1
                if depth <= 0:
                    return k if k + 1 < len(s) and (s[k + 1] == "=" or (s[k + 1] == "+" and k + 2 < len(s) and s[k + 2] == "=")) else -1
            k += 1
        return -1

    def lex_word(self, regex: bool = False, subscript: bool = False) -> Word:
        """regex=True lexes the right side of [[ x =~ re ]], where ( ) | < > are part of the word; subscript=True is
        command position, where name[a b]=v is one assignment word."""
        s = self.s
        parts: list = []
        lit: list[str] = []
        depth = 0
        subscript_end = self.subscript_end() if subscript else -1

        def flush():
            if lit:
                parts.append(Literal("".join(lit), False))
                lit.clear()

        while self.i < len(s):
            c = s[self.i]
            if c == "<" and self.peek(1) == "(":
                flush()
                close = self.find_close(self.i + 2)
                parts.append(ProcSub(s[self.i + 2:close]))
                self.i = close + 1
                continue
            # name=( ... ) and name+=( ... ): an array literal, kept as text until the assignment runs
            if c == "(" and not regex and not parts and len(lit) > 1 and lit[-1] == "=" and _is_name("".join(lit[:-1]).rstrip("+")):
                flush()
                end = self.find_close(self.i + 1)
                parts.append(ArrayLiteral(s[self.i + 1:end]))
                self.i = end + 1
                continue
            # extglob: @(a|b) !(x) *(x) +(x) ?(x) are one word; only `!(` at the start of a command is not
            if c == "(" and not regex and lit and lit[-1] in "@!*+?" and not (subscript and not parts and "".join(lit) == "!"):
                end = self.find_close(self.i + 1)
                lit.append(s[self.i:end + 1])
                self.i = end + 1
                continue
            if regex and c in "()":
                depth += 1 if c == "(" else -1
                if depth < 0:
                    break
            if c in _WORD_BREAK and not (regex and (c in "()|<>" or (depth > 0 and c in " \t"))) \
                    and not (self.i < subscript_end and c in " \t"):
                break
            if c == "\\":
                if self.i + 1 >= len(s):
                    lit.append("\\")
                    self.i += 1
                elif s[self.i + 1] == "\n":
                    self.i += 2
                else:
                    flush()
                    parts.append(Literal(s[self.i + 1], True))
                    self.i += 2
            elif c == "'":
                flush()
                j = s.find("'", self.i + 1)
                if j < 0:
                    raise ParseError("unexpected EOF while looking for matching `''")
                parts.append(Literal(s[self.i + 1:j], True))
                self.i = j + 1
            elif c == '"':
                flush()
                self.i += 1
                parts.extend(self.lex_quoted('"'))
            elif c == "$" and self.peek(1) == "'":
                flush()
                parts.append(Literal(self.lex_ansi_c(), True))
            elif c == "$" and self.peek(1) == '"':  # $"..." (locale translation) is just "..."
                flush()
                self.i += 2
                parts.extend(self.lex_quoted('"'))
            elif c == "$":
                flush()
                parts.append(self.lex_dollar(False))
            elif c == "`":
                flush()
                parts.append(self.lex_backtick(False))
            else:
                lit.append(c)
                self.i += 1
        flush()
        return Word(parts)

    def lex_ansi_c(self) -> str:
        """$'...': backslash escapes are decoded (\\n, \\t, \\x41, \\u00e9, \\0NN, \\', ...)."""
        s = self.s
        i = self.i + 2
        out: list[str] = []
        while i < len(s) and s[i] != "'":
            c = s[i]
            if c != "\\" or i + 1 >= len(s):
                out.append(c)
                i += 1
                continue
            n = s[i + 1]
            i += 2
            simple = {"a": "\a", "b": "\b", "e": "\x1b", "E": "\x1b", "f": "\f", "n": "\n", "r": "\r", "t": "\t",
                      "v": "\v", "\\": "\\", "'": "'", '"': '"', "?": "?"}
            if n in simple:
                out.append(simple[n])
            elif n in "01234567":
                j = i - 1
                while j < len(s) and j < i + 2 and s[j] in "01234567":
                    j += 1
                out.append(chr(int(s[i - 1:j], 8) & 0xFF))
                i = j
                if out[-1] == "\0":
                    i = self._skip_to_quote(i, out)
            elif n in "xuU":
                limit = {"x": 2, "u": 4, "U": 8}[n]
                j = i
                while j < len(s) and j < i + limit and s[j] in "0123456789abcdefABCDEF":
                    j += 1
                if j == i:
                    out.append("\\" + n)
                else:
                    code = int(s[i:j], 16)
                    out.append(chr(code) if code <= 0x10FFFF and not 0xD800 <= code <= 0xDFFF else "\ufffd")
                    i = j
                    if code == 0:
                        i = self._skip_to_quote(i, out)
            elif n == "c" and i < len(s):
                out.append(chr(ord(s[i].upper()) ^ 0x40))
                i += 1
            else:
                out.append("\\" + n)
        if i >= len(s):
            raise ParseError("unexpected EOF while looking for matching `''")
        self.i = i + 1
        return "".join(out)

    def _skip_to_quote(self, at: int, out: list) -> int:
        """bash strings cannot hold a NUL: $'a\\0b' is just "a"."""
        out.pop()
        s = self.s
        while at < len(s) and s[at] != "'":
            at += 2 if s[at] == "\\" else 1
        return at

    def lex_param_word(self, in_double: bool) -> list:
        """The word inside ${x op word}. Quotes and backslashes in it quote (so "$v" in a pattern is literal); inside an
        outer "...", a ' is an ordinary character. Parts are quoted only where the word itself quotes them."""
        s = self.s
        parts: list = []
        sb: list[str] = []

        def flush():
            if sb:
                parts.append(Literal("".join(sb), False))
                sb.clear()

        while self.i < len(s):
            c = s[self.i]
            if c == "\\" and self.i + 1 < len(s):
                n = s[self.i + 1]
                if n == "\n":
                    self.i += 2
                    continue
                # in double quotes only \$ \` \" \\ \} escape (and \& in a replacement); elsewhere any character does
                if in_double and n not in '$`"\\}&':
                    sb.append(c)
                    self.i += 1
                    continue
                flush()
                parts.append(Literal(n, True))
                self.i += 2
            elif c == "'" and not in_double:
                flush()
                j = s.find("'", self.i + 1)
                if j < 0:
                    raise ParseError("unexpected EOF while looking for matching `''")
                parts.append(Literal(s[self.i + 1:j], True))
                self.i = j + 1
            elif c == '"':
                flush()
                self.i += 1
                parts.extend(self.lex_quoted('"'))
            elif c == "$" and self.peek(1) == "'":
                flush()
                parts.append(Literal(self.lex_ansi_c(), True))
            elif c == "$":
                flush()
                parts.append(self.lex_dollar(False))
            elif c == "`":
                flush()
                parts.append(self.lex_backtick(False))
            else:
                sb.append(c)
                self.i += 1
        flush()
        if not parts:
            parts.append(Literal("", False))
        return parts

    def lex_quoted(self, terminator: str | None) -> list:
        """Double-quoted text (terminator '"') or a heredoc body / ${...} argument (terminator None)."""
        s = self.s
        parts: list = []
        sb: list[str] = []

        def flush():
            if sb:
                parts.append(Literal("".join(sb), True))
                sb.clear()

        while self.i < len(s) and s[self.i] != terminator:
            c = s[self.i]
            if c == "\\" and self.i + 1 < len(s) and (s[self.i + 1] in "$`\\\n" or s[self.i + 1] == terminator):
                if s[self.i + 1] != "\n":
                    sb.append(s[self.i + 1])
                self.i += 2
            elif c == "$":
                flush()
                parts.append(self.lex_dollar(True))
            elif c == "`":
                flush()
                parts.append(self.lex_backtick(True))
            else:
                sb.append(c)
                self.i += 1
        if terminator is not None:
            if self.i >= len(s):
                raise ParseError(f"unexpected EOF while looking for matching `{terminator}'")
            self.i += 1
        flush()
        if not parts:
            parts.append(Literal("", True))  # "" is still an argument
        return parts

    def lex_dollar(self, quoted: bool):
        s = self.s
        n = self.peek(1)
        if n == "(":
            if self.peek(2) == "(":
                close = self.find_arith_close(self.i + 3)
                if close >= 0:
                    expr = s[self.i + 3:close]
                    self.i = close + 2
                    return ArithSub(expr, quoted)
            close = self.find_close(self.i + 2)
            src = s[self.i + 2:close]
            self.i = close + 1
            return CommandSub(src, quoted)
        if n == "[":  # $[expr]: the old spelling of $((expr))
            depth, k = 0, self.i + 1
            while k < len(s):
                if s[k] == "[":
                    depth += 1
                elif s[k] == "]":
                    depth -= 1
                    if depth == 0:
                        break
                k += 1
            if k >= len(s):
                raise ParseError("unexpected EOF while looking for matching `]'")
            expr = s[self.i + 2:k]
            self.i = k + 1
            return ArithSub(expr, quoted)
        if n == "{":
            depth, k = 0, self.i + 2
            while k < len(s):
                if s[k] == "\\":
                    k += 2
                    continue
                if s[k] == "{":
                    depth += 1
                elif s[k] == "}":
                    if depth == 0:
                        break
                    depth -= 1
                k += 1
            if k >= len(s):
                raise ParseError("unexpected EOF while looking for matching `}'")
            name = s[self.i + 2:k]
            self.i = k + 1
            return Variable(name, quoted)
        if n.isalpha() or n == "_":
            j = self.i + 1
            while j < len(s) and (s[j].isalnum() or s[j] == "_"):
                j += 1
            name = s[self.i + 1:j]
            self.i = j
            return Variable(name, quoted)
        if n in "?$#!@*-" or n.isdigit():
            self.i += 2
            return Variable(n, quoted)
        self.i += 1
        return Literal("$", quoted)

    def lex_backtick(self, quoted: bool):
        s = self.s
        sb = []
        j = self.i + 1
        while j < len(s) and s[j] != "`":
            if s[j] == "\\" and j + 1 < len(s) and s[j + 1] in "`\\$":
                j += 1
            sb.append(s[j])
            j += 1
        if j >= len(s):
            raise ParseError("unexpected EOF while looking for matching ``'")
        self.i = j + 1
        return CommandSub("".join(sb), quoted)

    def find_close(self, k: int) -> int:
        """Index of the ')' closing a $( whose body starts at k."""
        s = self.s
        depth, from_ = 1, k
        # a case statement's patterns end in an unmatched ')': 0 = awaiting `in`, 1 = in patterns, 2 = in a body
        cases: list[int] = []
        seps = " \t\n;|&()"

        def keyword(at: int, w: str) -> bool:
            return s.startswith(w, at) and (at == from_ or s[at - 1] in seps) and (at + len(w) >= len(s) or s[at + len(w)] in seps)

        while k < len(s):
            c = s[k]
            if c.isalpha():
                if keyword(k, "case"):
                    cases.append(0)
                elif cases and cases[-1] == 0 and keyword(k, "in"):
                    cases[-1] = 1
                elif cases and keyword(k, "esac"):
                    cases.pop()
                else:
                    while k + 1 < len(s) and s[k + 1].isalnum():
                        k += 1
                    k += 1
                    continue
                while k + 1 < len(s) and s[k + 1].isalpha():
                    k += 1
                k += 1
                continue
            if cases and cases[-1] == 1 and c in "()":
                if c == ")":
                    cases[-1] = 2
                k += 1
                continue
            if cases and cases[-1] == 2 and c == ";" and k + 1 < len(s) and s[k + 1] in ";&":
                cases[-1] = 1
                k += 2
                continue
            if c == "\\":
                k += 1
            elif c == "'":
                k = s.find("'", k + 1)
                if k < 0:
                    k = len(s)
            elif c == '"':
                k += 1
                while k < len(s) and s[k] != '"':
                    if s[k] == "\\":
                        k += 1
                    k += 1
            elif c == "(":
                depth += 1
            elif c == ")":
                depth -= 1
                if depth == 0:
                    return k
            elif c == "<" and k + 2 < len(s) and s[k + 1] == "<" and s[k + 2] != "<" and (k == 0 or s[k - 1] != "<"):
                k = self._skip_heredoc(k + 2) - 1  # its body is text: quotes and parentheses in it mean nothing
            k += 1
        raise ParseError("unexpected EOF while looking for matching `)'")

    def _skip_heredoc(self, k: int) -> int:
        """From just after a `<<`, returns the index after the heredoc's closing delimiter line."""
        s = self.s
        strip_tabs = k < len(s) and s[k] == "-"
        if strip_tabs:
            k += 1
        while k < len(s) and s[k] in " \t":
            k += 1
        start = k
        while k < len(s) and not s[k].isspace() and s[k] not in ");|&":
            k += 1
        delimiter = s[start:k].replace("'", "").replace('"', "").replace("\\", "")
        line = s.find("\n", k)
        if not delimiter or line < 0:
            return k
        frm = line + 1
        while frm <= len(s):
            end = s.find("\n", frm)
            if end < 0:
                end = len(s)
            text = s[frm:end]
            if (text.lstrip("\t") if strip_tabs else text) == delimiter:
                return end
            frm = end + 1
        return len(s)


_RESERVED = {"then", "elif", "else", "fi", "do", "done", "esac", "}", "in"}
_CMD_START = {"then", "do", "else", "elif", "if", "while", "until", "{", "!", "time"}
COND_BINARY = {"==", "=", "!=", "=~", "<", ">", "-eq", "-ne", "-lt", "-le", "-gt", "-ge", "-nt", "-ot"}


def _is_name(s: str) -> bool:
    return bool(s) and (s[0].isalpha() or s[0] == "_") and all(c.isalnum() or c == "_" for c in s)


class Parser:
    def __init__(self, tokens: list[Token], source: str = ""):
        self.t = tokens
        self.p = 0
        self.src = source

    @staticmethod
    def parse(source: str) -> Script:
        try:
            p = Parser(Lexer(source).lex(), source)
            script = p.parse_script()
        except RecursionError:  # deeply nested input must not crash the host
            raise ParseError("command is nested too deeply") from None
        if p.p < len(p.t):
            raise p.unexpected()
        return script

    @staticmethod
    def parse_words(source: str) -> list[Word]:
        """Lexes a string into plain words (used for alias values)."""
        return [t.word for t in Lexer(source).lex() if t.kind == "word"]

    @property
    def cur(self) -> Token | None:
        return self.t[self.p] if self.p < len(self.t) else None

    def at(self, o: int) -> Token | None:
        return self.t[self.p + o] if self.p + o < len(self.t) else None

    def is_op(self, op: str) -> bool:
        return self.cur is not None and self.cur.kind == "op" and self.cur.op == op

    def is_word(self, w: str) -> bool:
        return self.cur is not None and self.cur.kind == "word" and self.cur.word.is_(w)

    def skip_newlines(self) -> None:
        while self.is_op("\n"):
            self.p += 1

    def skip_separators(self) -> None:
        while self.is_op("\n") or self.is_op(";"):
            self.p += 1

    def unexpected(self) -> ParseError:
        return ParseError(f"syntax error near unexpected token `{self.cur if self.cur else 'newline'}'")

    def expect(self, word: str) -> None:
        if not self.is_word(word):
            if self.cur is None:
                raise ParseError(f"syntax error: unexpected end of file (expected `{word}')")
            raise self.unexpected()
        self.p += 1

    def at_terminator(self, terms) -> bool:
        c = self.cur
        if c is None:
            return False
        if c.kind == "word":
            return c.word.unquoted_literal in terms
        return c.kind == "op" and c.op in terms

    def parse_script(self, *terms: str) -> Script:
        items = []
        while True:
            while self.is_op("\n") or self.is_op(";") or self.is_op("&"):
                self.p += 1
            if self.cur is None or self.at_terminator(terms):
                break
            item = self.parse_and_or()
            if self.is_op("&"):
                item.background = True
            items.append(item)
            if self.cur is None or self.is_op("\n") or self.is_op(";") or self.is_op("&"):
                continue
            if self.at_terminator(terms):
                break
            raise self.unexpected()
        return Script(items)

    def parse_and_or(self) -> AndOr:
        first = self.parse_pipeline()
        rest = []
        while self.is_op("&&") or self.is_op("||"):
            op = self.cur.op
            self.p += 1
            self.skip_newlines()
            rest.append((op, self.parse_pipeline()))
        return AndOr(first, rest)

    def parse_pipeline(self) -> Pipeline:
        timed = posix = False
        if self.is_word("time"):
            timed = True
            self.p += 1
            if self.is_word("-p"):
                posix = True
                self.p += 1
            if self.cur is None or (self.cur.kind == "op" and self.cur.op in ("\n", ";", "&", "&&", "||", ")")):
                return Pipeline([], False, True, posix)  # `time` alone just reports
        negate = False
        if self.is_word("!"):
            negate = True
            self.p += 1
        cmds = [self.parse_command()]
        while self.is_op("|"):
            self.p += 1
            self.skip_newlines()
            cmds.append(self.parse_command())
        return Pipeline(cmds, negate, timed, posix)

    def with_redirects(self, cmd):
        cmd.redirects = self.parse_redirects([])
        return cmd

    def parse_command(self):
        c = self.cur
        if c is not None and c.kind == "arith":
            self.p += 1
            return self.with_redirects(ArithCommand(c.op))
        if self.is_op("(") or self.is_word("{"):
            sub = self.is_op("(")
            self.p += 1
            body = self.parse_script(")" if sub else "}")
            if not (self.is_op(")") if sub else self.is_word("}")):
                raise ParseError("syntax error: unexpected end of file") if self.cur is None else self.unexpected()
            self.p += 1
            return self.with_redirects(GroupCommand(body, sub))
        kw = c.word.unquoted_literal if c is not None and c.kind == "word" else None
        if kw is not None:
            if kw == "if":
                return self.with_redirects(self.parse_if())
            if kw in ("while", "until"):
                return self.with_redirects(self.parse_while(kw == "until"))
            if kw == "for":
                return self.with_redirects(self.parse_for())
            if kw == "case":
                return self.with_redirects(self.parse_case())
            if kw == "function":
                return self.parse_function(True)
            if kw == "[[":
                return self.with_redirects(self.parse_cond())
            if kw == "select":
                return self.with_redirects(self.parse_for(select=True))
            if kw == "coproc":
                raise ParseError("'coproc' is not supported by this shell")
            if kw in _RESERVED:
                raise self.unexpected()
            a1, a2 = self.at(1), self.at(2)
            if a1 is not None and a1.kind == "op" and a1.op == "(" and a2 is not None and a2.kind == "op" and a2.op == ")":
                return self.parse_function(False)

        line = self.cur.line if self.cur is not None else 0
        assignments, words, redirects = [], [], []
        while self.cur is not None:
            if self.cur.kind == "redirect":
                self.parse_redirects(redirects)
                continue
            if self.cur.kind != "word":
                break
            w = self.cur.word
            self.p += 1
            if not words:
                a = _try_assignment(w)
                if a:
                    assignments.append(a)
                    continue
            words.append(w)
        if not words and not assignments and not redirects:
            raise self.unexpected()
        return SimpleCommand(assignments, words, redirects, line)

    def parse_list(self, *terms: str) -> Script:
        """A compound command's list, which bash requires to hold at least one command (`if; then` is an error)."""
        lst = self.parse_script(*terms)
        if not lst.items and self.cur is not None:
            raise self.unexpected()
        return lst

    def parse_if(self) -> IfCommand:
        self.p += 1
        branches = []
        cond = self.parse_list("then")
        self.expect("then")
        branches.append((cond, self.parse_list("elif", "else", "fi")))
        else_body = None
        while True:
            if self.is_word("elif"):
                self.p += 1
                cond = self.parse_list("then")
                self.expect("then")
                branches.append((cond, self.parse_list("elif", "else", "fi")))
                continue
            if self.is_word("else"):
                self.p += 1
                else_body = self.parse_list("fi")
            break
        self.expect("fi")
        return IfCommand(branches, else_body)

    def parse_while(self, until: bool) -> LoopCommand:
        self.p += 1
        cond = self.parse_list("do")
        self.expect("do")
        body = self.parse_list("done")
        self.expect("done")
        return LoopCommand(cond, body, until)

    def parse_for(self, select: bool = False):
        self.p += 1
        c = self.cur
        if not select and c is not None and c.kind == "arith":
            self.p += 1
            parts = c.op.split(";")
            if len(parts) != 3:
                raise ParseError("syntax error: expected `for (( init; cond; step ))'")
            self.skip_separators()
            self.expect("do")
            body = self.parse_script("done")
            self.expect("done")
            return ArithForCommand(parts[0].strip(), parts[1].strip(), parts[2].strip(), body)
        name = c.word.unquoted_literal if c is not None and c.kind == "word" else None
        if not name or not _is_name(name):
            raise self.unexpected()
        self.p += 1
        self.skip_newlines()
        items = None
        if self.is_word("in"):
            self.p += 1
            items = []
            while self.cur is not None and self.cur.kind == "word":
                items.append(self.cur.word)
                self.p += 1
        self.skip_separators()
        self.expect("do")
        body = self.parse_script("done")
        self.expect("done")
        return ForCommand(name, items, body, select=select)

    def parse_case(self) -> CaseCommand:
        self.p += 1
        if self.cur is None or self.cur.kind != "word":
            raise self.unexpected()
        subject = self.cur.word
        self.p += 1
        self.skip_newlines()
        self.expect("in")
        self.skip_newlines()
        clauses = []
        while not self.is_word("esac"):
            if self.cur is None:
                raise ParseError("syntax error: unexpected end of file (expected `esac')")
            if self.is_op("("):
                self.p += 1
            patterns = []
            while True:
                if self.cur is None or self.cur.kind != "word":
                    raise self.unexpected()
                patterns.append(self.cur.word)
                self.p += 1
                if self.is_op("|"):
                    self.p += 1
                    continue
                break
            if not self.is_op(")"):
                raise self.unexpected()
            self.p += 1
            body = self.parse_script(";;", ";&", ";;&", "esac")
            terminator = ";;"
            if self.cur is not None and self.cur.kind == "op" and self.cur.op in (";;", ";&", ";;&"):
                terminator = self.cur.op
                self.p += 1
            self.skip_newlines()
            clauses.append((patterns, body, terminator))
        self.p += 1
        return CaseCommand(subject, clauses)

    def parse_function(self, keyword: bool) -> FunctionDef:
        if keyword:
            self.p += 1
        name = self.cur.word.unquoted_literal if self.cur is not None and self.cur.kind == "word" else None
        if not name:
            raise self.unexpected()
        self.p += 1
        if self.is_op("("):
            self.p += 1
            if not self.is_op(")"):
                raise self.unexpected()
            self.p += 1
        self.skip_newlines()
        if self.cur is None:
            raise ParseError("syntax error: unexpected end of file")
        start = self.cur.start
        body = self.parse_command()
        return FunctionDef(name, body, source=self.src[start:max(start, self.t[self.p - 1].end)].rstrip())

    def parse_cond(self) -> CondCommand:
        self.p += 1
        items = []
        while not self.is_word("]]"):
            c = self.cur
            if c is None:
                raise ParseError("syntax error: unexpected end of file (expected `]]')")
            if c.kind == "word":
                items.append(c.word)
            elif c.kind == "op" and c.op in ("&&", "||", "(", ")"):
                items.append(Word.lit(c.op))
            elif c.kind == "redirect" and c.redirect.op in ("<", ">") and c.redirect.here is None:
                items.append(Word.lit(c.redirect.op))
            else:
                raise self.unexpected()
            self.p += 1
        self.p += 1
        return CondCommand(items)

    def parse_redirects(self, into: list) -> list:
        while self.cur is not None and self.cur.kind == "redirect":
            r = self.cur.redirect
            self.p += 1
            if r.here is None:
                if self.cur is None or self.cur.kind != "word":
                    raise self.unexpected()
                r = Redirect(r.fd, r.op, self.cur.word, fd_variable=r.fd_variable)
                self.p += 1
            into.append(r)
        return into


def is_cond_binary(w: Word) -> bool:
    return w.unquoted_literal in COND_BINARY


def is_name(s: str) -> bool:
    return _is_name(s)


def split_subscript(w: Word, offset: int):
    """Splits the "sub]=value" that follows an opening bracket. The subscript may span several parts (arr[$i]=x,
    m["a b"]=x), so this looks for the first unquoted "]=" or "]+=". Returns (index, value, append) or None."""
    index: list = []
    for i, p in enumerate(w.parts):
        if not isinstance(p, Literal) or p.quoted:
            index.append(p)
            continue
        text = p.text[offset:] if i == 0 else p.text
        plain, plus = text.find("]="), text.find("]+=")
        append = plus >= 0 and (plain < 0 or plus < plain)
        at = plus if append else plain
        if at < 0:
            if text:
                index.append(Literal(text, False))
            continue
        if at > 0:
            index.append(Literal(text[:at], False))
        value: list = []
        tail = text[at + (3 if append else 2):]
        if tail:
            value.append(Literal(tail, False))
        value.extend(w.parts[i + 1:])
        return Word(index), Word(value), append
    return None


def try_assignment(w: Word):
    return _try_assignment(w)


def _try_assignment(w: Word):
    if not w.parts or not isinstance(w.parts[0], Literal) or w.parts[0].quoted:
        return None
    first = w.parts[0].text
    eq = first.find("=")
    open_ = first.find("[")
    if open_ > 0 and (eq < 0 or open_ < eq) and _is_name(first[:open_]):
        r = split_subscript(w, open_ + 1)
        return Assignment(first[:open_], r[1], index=r[0], append=r[2]) if r else None
    if eq <= 0:
        return None
    name = first[:eq]
    append = name.endswith("+")
    if append:
        name = name[:-1]
    if not _is_name(name):
        return None
    if len(w.parts) == 2 and isinstance(w.parts[1], ArrayLiteral) and eq == len(first) - 1:
        return Assignment(name, Word([]), elements=w.parts[1].source, append=append)
    rest = []
    if eq + 1 < len(first):
        rest.append(Literal(first[eq + 1:], False))
    rest.extend(w.parts[1:])
    return Assignment(name, Word(rest), append=append)
