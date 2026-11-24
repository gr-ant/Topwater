"""Clean-room lexer and parser for a practical subset of POSIX sh / bash syntax."""
from __future__ import annotations

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


# ---------- AST: every command has .redirects ----------

@dataclass
class SimpleCommand:
    assignments: list  # list[tuple[str, Word]]
    words: list
    redirects: list = field(default_factory=list)


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
    clauses: list  # list[tuple[list[Word], Script]]
    redirects: list = field(default_factory=list)


@dataclass
class FunctionDef:
    name: str
    body: object
    redirects: list = field(default_factory=list)


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


@dataclass
class AndOr:
    first: Pipeline
    rest: list  # list[tuple[str, Pipeline]]


@dataclass
class Script:
    items: list


@dataclass
class Token:
    kind: str  # "word" | "op" | "redirect" | "arith"
    op: str = ""
    word: Word | None = None
    redirect: Redirect | None = None

    def __str__(self) -> str:
        if self.kind == "word":
            return str(self.word)
        if self.kind == "op":
            return "newline" if self.op == "\n" else self.op
        if self.kind == "arith":
            return "(("
        return self.redirect.op


_WORD_BREAK = set(" \t\r\n|&;()<>")


class Lexer:
    def __init__(self, s: str):
        self.s = s
        self.i = 0
        self.pending: list[HereDoc] = []

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
            c = s[self.i]
            if c == "#":
                while self.i < len(s) and s[self.i] != "\n":
                    self.i += 1
                continue
            if c == "\n":
                self.i += 1
                tokens.append(Token("op", "\n"))
                self.read_heredoc_bodies()
                continue
            if c.isdigit():
                j = self.i
                while j < len(s) and s[j].isdigit():
                    j += 1
                if j < len(s) and s[j] in "<>":
                    fd = int(s[self.i:j])
                    self.i = j
                    tokens.append(self.lex_redirect(fd))
                    continue
            if c == "&" and self.peek(1) == ">":
                self.i += 2
                append = self.peek() == ">"
                if append:
                    self.i += 1
                tokens.append(Token("redirect", redirect=Redirect(1, "&>>" if append else "&>")))
                continue
            if c == "<" and self.peek(1) == "(":
                tokens.append(Token("word", word=self.lex_word()))
                continue
            if c in "<>":
                tokens.append(self.lex_redirect(1 if c == ">" else 0))
                continue
            if c == "(" and self.peek(1) == "(":
                close = self.find_arith_close(self.i + 2)
                if close >= 0:
                    tokens.append(Token("arith", s[self.i + 2:close]))
                    self.i = close + 2
                    continue
            if (c == "&" and self.peek(1) == "&") or (c == "|" and self.peek(1) == "|") or (c == ";" and self.peek(1) == ";"):
                self.i += 2
                tokens.append(Token("op", c * 2))
                continue
            if c in "|&;()":
                self.i += 1
                tokens.append(Token("op", c))
                continue
            tokens.append(Token("word", word=self.lex_word()))
        self.read_heredoc_bodies()  # heredoc on the last line with no trailing newline: body is empty
        return tokens

    def find_arith_close(self, k: int) -> int:
        """Index of the '))' closing an arithmetic expression whose body starts at k, or -1."""
        s = self.s
        depth = 0
        while k < len(s):
            if s[k] == "(":
                depth += 1
            elif s[k] == ")":
                if depth:
                    depth -= 1
                else:
                    return k if k + 1 < len(s) and s[k + 1] == ")" else -1
            k += 1
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
        if op == ">|":
            op = ">"

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
            return Token("redirect", redirect=Redirect(0, "<<", here=doc))
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

    def lex_word(self) -> Word:
        s = self.s
        parts: list = []
        lit: list[str] = []

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
            if c in _WORD_BREAK:
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
        if n in "?$#!@*" or n.isdigit():
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
        depth = 1
        while k < len(s):
            c = s[k]
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
            k += 1
        raise ParseError("unexpected EOF while looking for matching `)'")


_RESERVED = {"then", "elif", "else", "fi", "do", "done", "esac", "}"}
COND_BINARY = {"==", "=", "!=", "=~", "<", ">", "-eq", "-ne", "-lt", "-le", "-gt", "-ge", "-nt", "-ot"}


def _is_name(s: str) -> bool:
    return bool(s) and (s[0].isalpha() or s[0] == "_") and all(c.isalnum() or c == "_" for c in s)


class Parser:
    def __init__(self, tokens: list[Token]):
        self.t = tokens
        self.p = 0

    @staticmethod
    def parse(source: str) -> Script:
        p = Parser(Lexer(source).lex())
        script = p.parse_script()
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
            items.append(self.parse_and_or())
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
        negate = False
        if self.is_word("!"):
            negate = True
            self.p += 1
        cmds = [self.parse_command()]
        while self.is_op("|"):
            self.p += 1
            self.skip_newlines()
            cmds.append(self.parse_command())
        return Pipeline(cmds, negate)

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
                raise ParseError("'select' is not supported by this shell")
            if kw in _RESERVED:
                raise self.unexpected()
            a1, a2 = self.at(1), self.at(2)
            if a1 is not None and a1.kind == "op" and a1.op == "(" and a2 is not None and a2.kind == "op" and a2.op == ")":
                return self.parse_function(False)

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
        return SimpleCommand(assignments, words, redirects)

    def parse_if(self) -> IfCommand:
        self.p += 1
        branches = []
        cond = self.parse_script("then")
        self.expect("then")
        branches.append((cond, self.parse_script("elif", "else", "fi")))
        else_body = None
        while True:
            if self.is_word("elif"):
                self.p += 1
                cond = self.parse_script("then")
                self.expect("then")
                branches.append((cond, self.parse_script("elif", "else", "fi")))
                continue
            if self.is_word("else"):
                self.p += 1
                else_body = self.parse_script("fi")
            break
        self.expect("fi")
        return IfCommand(branches, else_body)

    def parse_while(self, until: bool) -> LoopCommand:
        self.p += 1
        cond = self.parse_script("do")
        self.expect("do")
        body = self.parse_script("done")
        self.expect("done")
        return LoopCommand(cond, body, until)

    def parse_for(self):
        self.p += 1
        c = self.cur
        if c is not None and c.kind == "arith":
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
        return ForCommand(name, items, body)

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
            body = self.parse_script(";;", "esac")
            if self.is_op(";;"):
                self.p += 1
            self.skip_newlines()
            clauses.append((patterns, body))
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
        return FunctionDef(name, self.parse_command())

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
                r = Redirect(r.fd, r.op, self.cur.word)
                self.p += 1
            into.append(r)
        return into


def is_cond_binary(w: Word) -> bool:
    return w.unquoted_literal in COND_BINARY


def _try_assignment(w: Word):
    if not w.parts or not isinstance(w.parts[0], Literal) or w.parts[0].quoted:
        return None
    first = w.parts[0].text
    eq = first.find("=")
    if eq <= 0:
        return None
    name = first[:eq]
    append = name.endswith("+")
    if append:
        name = name[:-1]
    if not _is_name(name):
        return None
    rest = []
    if append:
        rest.append(Variable(name, True))  # VAR+=x  ==  VAR="$VAR"x
    if eq + 1 < len(first):
        rest.append(Literal(first[eq + 1:], False))
    rest.extend(w.parts[1:])
    return name, Word(rest)
