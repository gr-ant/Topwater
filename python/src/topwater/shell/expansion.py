"""Shell: word expansion, parameters, arithmetic, command and process substitution, field splitting. See docs/architecture.md."""
from __future__ import annotations

import re
from dataclasses import replace

from .. import arith
from .._spec import DIAGNOSTICS, diagnostic
from ..glob import _cached, brace_expand, glob_escape, glob_expand, glob_to_regex, glob_to_regex_body, has_wildcard
from ..parser import (
    ArithSub, CommandSub, ExpandedText, Lexer, Literal, ParseError, Parser, ProcSub, SimpleCommand, Variable, Word,
    is_name,
)
from ..vfs import ShellError
from .invocation import quote_value
from .redirection import CappedWriter
from .types import ExitSignal, LimitExceeded, StdinBuffer, _BadSubstitution, _Io, _LineAbort, default_op
from .variables import _SCRATCH, array_ref


_PARAM = re.compile(r"^([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[@*#?$!-])(.*)$", re.S)
_INDIRECT = re.compile(r"^!([A-Za-z_][A-Za-z0-9_]*|[0-9]+|[@*#?$!-])(.*)$", re.S)
_PREFIX = re.compile(r"^!([A-Za-z_][A-Za-z0-9_]*)[*@]$")
_ASSIGNMENT_TILDE = re.compile(r"(?:(?<=^)|(?<=:))~([A-Za-z0-9_.-]*)(?=/|:|$)")


class ExpansionPart:
    # ---------- parts and arithmetic ----------

    async def _expand_part(self, part, io_: _Io) -> str:
        if isinstance(part, (Literal, ExpandedText)):
            return part.text
        if isinstance(part, Variable):
            try:
                return await self._expand_parameter(part.name, io_, part.quoted)
            except _BadSubstitution as e:
                message = diagnostic("badSubstitution", text=self._bad_substitution_text(part))
                if e.fatal:
                    raise self._fatal(io_, message, 127) from None
                io_.err.write(f"{self.err_prefix}{message}\n")
                raise _LineAbort() from None
        if isinstance(part, CommandSub):
            return await self._capture(part.source, io_)
        if isinstance(part, ArithSub):
            return str(await self._arith_expansion(part.expr, io_))
        if isinstance(part, ProcSub):
            return await self._process_substitution(part.source, io_)
        return ""

    async def _arith(self, expr: str, io_: _Io) -> int:
        """Expands $(cmd), `cmd` and ${...} inside an arithmetic expression, then evaluates it."""
        # each level of $(( $(( ... )) )) re-reads what is inside it, so unbounded nesting is quadratic work
        self._arith_depth += 1
        if self._arith_depth > 100:
            self._arith_depth = 0
            raise LimitExceeded("arithmetic is nested too deeply")
        try:
            # re-lexing each level costs O(n), so measure deep $(( $(( ... )) )) in one pass before expanding it
            if self._arith_depth == 1 and expr.count("$((") >= 100 and _arith_nesting(expr) > 100:
                raise LimitExceeded("arithmetic is nested too deeply")
            if "$" in expr or "`" in expr:
                expr = await self._expand_single(Word(Lexer(expr).lex_quoted(None)), io_)
            try:
                return arith.evaluate(self, expr)
            except RecursionError:
                raise ShellError(DIAGNOSTICS["expressionNested"]) from None
        finally:
            if self._arith_depth > 0:
                self._arith_depth -= 1

    async def _arith_expansion(self, expr: str, io_: _Io) -> int:
        """$(( )): unlike (( )) and let, an error here ends the script, as in bash."""
        try:
            return await self._arith(expr, io_)
        except ShellError as e:
            raise self._fatal(io_, str(e), 1) from None

    # ---------- parameters ----------

    def _bad_substitution_text(self, part: Variable) -> str:
        """What bash names in a bad substitution: the double-quoted string around it, or else the whole word."""
        parts = self._cur_word.parts if self._cur_word is not None else []
        at = next((i for i, p in enumerate(parts) if p is part), None)
        if at is None:
            return "${" + part.name + "}"
        if not part.quoted:
            return str(self._cur_word)
        # shortcut: adjacent "..." strings join ("a""b"), and $x prints as ${x}
        def in_dq(p) -> bool:
            return p.quoted and (not isinstance(p, Literal) or p.in_double_quotes)

        lo, hi = at, at + 1
        while lo > 0 and in_dq(parts[lo - 1]):
            lo -= 1
        while hi < len(parts) and in_dq(parts[hi]):
            hi += 1
        return str(Word(parts[lo:hi]))

    def _fatal(self, io_: _Io, message: str, code: int) -> ExitSignal:
        """An expansion error that ends a non-interactive bash: the shell exits (a subshell with status 1)."""
        io_.err.write(f"{self.err_prefix}{message}\n")
        return ExitSignal(1 if self._subshell > 0 else code)

    def _indirect_target(self, expr: str) -> str | None:
        """${!name...}: the expression with name's value in its place; None if expr is not an indirection."""
        if len(expr) < 2 or expr[0] != "!" or _PREFIX.match(expr):
            return None
        r = array_ref(expr)
        if r is not None and r[0]:
            return None
        m = _INDIRECT.match(expr)
        if not m:
            return None
        name = m.group(1)
        if self.has_attr(name, "n"):
            return None  # ${!ref} of a nameref is the name it refers to
        target = self.get_variable(name)
        if not target:
            raise ShellError(diagnostic("invalidIndirect", name=name))
        return target + m.group(2)

    def _param_word(self, text: str, quoted: bool) -> Word:
        """The word of ${x op word}, lexed once per distinct text: quotes in it apply, and inside "..." a ' is literal."""
        key = (text, quoted)
        w = self._param_words.get(key)
        if w is not None:
            return w
        if len(self._param_words) >= 512:
            self._param_words.clear()
        try:
            w = Word(Lexer(text).lex_param_word(quoted))
        except ParseError as e:
            raise ShellError(str(e)) from None
        self._param_words[key] = w
        return w

    async def _param_word_expand(self, text: str, quoted: bool, io_: _Io) -> str:
        return await self._expand_single(self._param_word(text, quoted), io_)

    async def _replacement(self, text: str, quoted: bool, io_: _Io) -> list:
        """${x/pat/rep}'s replacement as pieces: text, or None where an unquoted & stands for the match (bash 5.2)."""
        pieces: list = []
        first = True
        for part in self._param_word(text, quoted).parts:
            t = await self._expand_part(part, io_)
            if first and isinstance(part, Literal) and not part.quoted:
                tp = self._tilde_prefix(t)
                if tp:
                    t = tp[0] + t[tp[1]:]
            first = False
            if part.quoted or "&" not in t:
                pieces.append(t)
            else:
                for k, chunk in enumerate(t.split("&")):
                    if k:
                        pieces.append(None)
                    pieces.append(chunk)
        return pieces

    async def _expand_parameter(self, expr: str, io_: _Io, quoted: bool = False) -> str:
        """${name}, ${#name}, ${name:-x}, ${name%pat}, ${name/a/b}, ${name:1:2}, ${name^^}, ${name@Q}, ${!name} ..."""
        if len(expr) > 1 and expr[0] == "!":
            pm = _PREFIX.match(expr)
            if pm:
                return " ".join(sorted({k for k in list(self.env) + list(self.arrays) if k.startswith(pm.group(1)) and k != _SCRATCH}))
            if is_name(expr[1:]) and self.has_attr(expr[1:], "n"):
                return self.env.get(expr[1:], "")
            target = self._indirect_target(expr)
            if target is not None:
                return await self._expand_parameter(target, io_, quoted)
        if len(expr) > 1 and expr[0] == "#":
            counted = array_ref(expr[1:])
            if counted is not None and not counted[0] and counted[3] == "":
                if counted[2] in ("@", "*"):
                    a = self.get_array(counted[1])
                    return str(len(a.items) if a is not None else 1 if self._look_up(counted[1]) is not None else 0)
                return str(len(self.get_element(counted[1], await self._subscript(counted[2], io_)) or ""))
        # as one string, ${a[*]} joins with the first character of IFS and ${a[@]} with a space
        all_ = await self._array_elements(expr, io_, quoted)
        if all_ is not None:
            return (self._ifs_joiner if array_ref(expr)[2] == "*" else " ").join(all_)
        one = array_ref(expr)
        if one is not None and not one[0]:
            try:
                element = self.get_element(one[1], await self._subscript(one[2], io_))
            except arith.ArithError as e:
                raise self._fatal(io_, str(e), 1) from None
            except ShellError as e:
                io_.err.write(f"{self.err_prefix}{e}\n")  # like bash: reported, and the expansion is empty
                element = None
            return await self._element_op(element, one[3], io_, quoted)
        if len(expr) > 1 and expr[0] == "#":
            if expr[1:] in ("@", "*"):
                return str(len(self.positional))
            self._check_set(expr[1:], io_)
            return str(len(self.get_variable(expr[1:])))
        m = _PARAM.match(expr)
        if not m:
            raise _BadSubstitution(expr)
        name, rest = m.group(1), m.group(2)
        value = self.get_variable(name)
        d = default_op(rest)
        if d is None:
            self._check_set(name, io_)  # defaults handle unset variables
        if not rest:
            return value

        if d is not None:
            op, word = d
            missing = not value if op[0] == ":" else not self._is_set(name)
            kind = op[-1]
            if kind == "-":
                return await self._param_word_expand(word, quoted, io_) if missing else value
            if kind == "=":
                if not missing:
                    return value
                if not is_name(name):
                    raise ShellError(diagnostic("cannotAssign", name=name))
                self.set_var(name, await self._param_word_expand(word, quoted, io_))
                return self.get_variable(name)
            if kind == "+":
                return "" if missing else await self._param_word_expand(word, quoted, io_)
            if missing:
                msg = (await self._param_word_expand(word, quoted, io_) if word
                       else "parameter null or not set" if op[0] == ":" else "parameter not set")
                raise self._fatal(io_, f"{name}: {msg}", 127)
            return value
        if rest[0] in "#%":
            longest = len(rest) > 1 and rest[1] == rest[0]
            rx = glob_to_regex(await self._pattern(self._param_word(rest[2 if longest else 1:], quoted), io_), slashes=True)
            n = len(value)
            if rest[0] == "#":
                for i in (range(n, -1, -1) if longest else range(n + 1)):
                    if rx.match(value[:i]):
                        return value[i:]
            else:
                for i in (range(n + 1) if longest else range(n, -1, -1)):
                    if rx.match(value[i:]):
                        return value[:i]
            return value
        if rest[0] == "/":
            body = rest[1:]
            mode = body[0] if body and body[0] in "/#%" else ""
            if mode:
                body = body[1:]
            # the pattern ends at the first unquoted '/'; after `//` a leading '/' is part of the pattern
            slash = -1
            i = 1 if mode == "/" and body.startswith("/") else 0
            while i < len(body):
                ch = body[i]
                if ch == "\\":
                    i += 2
                    continue
                if ch == "'" and not quoted:
                    q = body.find("'", i + 1)
                    i = (q if q >= 0 else len(body)) + 1
                    continue
                if ch == '"':
                    i += 1
                    while i < len(body) and body[i] != '"':
                        if body[i] == "\\":
                            i += 1
                        i += 1
                    i += 1
                    continue
                if ch == "/":
                    slash = i
                    break
                i += 1
            pat = glob_to_regex_body(await self._pattern(self._param_word(body if slash < 0 else body[:slash], quoted), io_), slashes=True)
            rep = [] if slash < 0 else await self._replacement(body[slash + 1:], quoted, io_)

            def sub(m: re.Match) -> str:
                return "".join(m.group(0) if p is None else p for p in rep)

            # an empty pattern matches nothing, except at an anchor: ${x/#/pre-} and ${x/%/-post} add to the ends
            if not pat:
                plain = "".join("" if p is None else p for p in rep)
                return plain + value if mode == "#" else value + plain if mode == "%" else value
            rx = _cached(("^(?:" + pat + ")") if mode == "#" else ("(?:" + pat + ")$") if mode == "%" else pat, re.S)
            # ${x//?/$x} grows with the square of x: refuse before building it
            rep_len = sum(len(p) for p in rep if p is not None)
            if mode == "/" and len(value) + rep_len * (len(value) + 1) > self.max_string_bytes \
                    and len(value) + rep_len * len(rx.findall(value)) > self.max_string_bytes:
                raise self.too_large("expansion", "write the data to a file instead")
            return rx.sub(sub, value, count=0 if mode == "/" else 1)
        if rest[0] in "^,~":
            # ${x^} ${x^^} ${x,} ${x,,} ${x~} ${x~~}, optionally only for characters matching a pattern: ${x^^[aeiou]}
            every = len(rest) > 1 and rest[1] == rest[0]
            tail = rest[2 if every else 1:]
            only = glob_to_regex(await self._pattern(self._param_word(tail, quoted), io_), slashes=True) if tail else None
            chars = list(value)
            for i in range(len(chars)):
                if not every and i > 0:
                    break
                if only is None or only.match(chars[i]):
                    up = rest[0] == "^" or (rest[0] == "~" and chars[i].islower())
                    chars[i] = chars[i].upper() if up else chars[i].lower()
            return "".join(chars)
        if len(rest) == 2 and rest[0] == "@":
            from ..commands import unescape
            from ..commands.more import attribute_flags
            if rest[1] == "a":
                return attribute_flags(self, self.resolve_name(name))
            if not self._is_set(name):
                return ""
            c = rest[1]
            if c in "QK":
                return quote_value(value)
            if c == "E":
                return unescape(value, keep_unknown=True)
            if c == "U":
                return value.upper()
            if c == "L":
                return value.lower()
            if c == "u":
                return value[:1].upper() + value[1:]
            if c == "A":
                flags = attribute_flags(self, self.resolve_name(name))
                return f"declare -{flags} {name}={quote_value(value)}" if flags else f"{name}={quote_value(value)}"
            if c == "P":
                return value
            raise _BadSubstitution(expr, fatal=True)
        if rest[0] == ":":
            spec = rest[1:].split(":", 1)
            offset = await self._arith(spec[0], io_)
            if offset < 0:
                offset += len(value)
            if offset < 0 or offset > len(value):
                return ""
            tail = value[offset:]
            if len(spec) == 1:
                return tail
            length = await self._arith(spec[1], io_)
            if length < 0:
                length += len(tail)
                if length < 0:
                    raise ShellError(diagnostic("substringNegative", expression=spec[1].strip()))
            return tail[:length]
        if rest == "@":  # ${x@}: nothing on an unset variable, the fatal error on a set one
            if not self._is_set(name):
                return ""
            raise _BadSubstitution(expr, fatal=True)
        raise _BadSubstitution(expr)

    def _check_set(self, name: str, io_: _Io) -> None:
        """set -u: expanding an unset variable is an error that ends the (sub)shell, like bash."""
        if not self.nounset or name in ("@", "*") or self._is_set(name):
            return
        raise self._fatal(io_, diagnostic("unboundVariable", name=name), 127)

    # ---------- substitution ----------

    async def _run_substitution(self, source: str, io_: _Io) -> int:
        """$(...) and <(...) run in a subshell that, as in bash, does not inherit set -e (unless shopt inherit_errexit)."""
        errexit = self.errexit
        if "inherit_errexit" not in self.shopts:
            self.errexit = False
        try:
            return await self._run_script_text(source, io_, subshell=True)
        finally:
            self.errexit = errexit

    async def _process_substitution(self, source: str, io_: _Io) -> str:
        """<(cmd): writes cmd's output to /dev/fd/N (removed after the tool call) and returns that path."""
        out = CappedWriter(self)
        await self._run_substitution(source, replace(io_, stdin=StdinBuffer(), out=out))
        path = f"/dev/fd/{63 + self._proc_sub_count}"
        self._proc_sub_count += 1
        with self.fs.as_root():
            self.fs.write_text(path, out.getvalue())
        return path

    async def _capture(self, source: str, io_: _Io) -> str:
        file_word = _read_file_word(source)
        if file_word is not None:  # $(< file) is the file's contents
            target = await self._expand_single(file_word, io_)
            try:
                text = self.fs.read_text(self.resolve(target))
                self.last_exit_code = 0
            except ShellError as e:
                io_.err.write(f"{self.err_prefix}{target}: {str(e).rsplit(': ', 1)[-1]}\n")
                text, self.last_exit_code = "", 1
            return text.rstrip("\n")
        out = CappedWriter(self)
        self.last_exit_code = await self._run_substitution(source, replace(io_, stdin=StdinBuffer(), out=out))
        return out.getvalue().rstrip("\n")

    # ---------- words ----------

    async def _expand_single(self, word: Word, io_: _Io) -> str:
        """Expands a word without field splitting or globbing (redirect targets, heredocs, case subjects)."""
        out = []
        total = 0
        saved, self._cur_word = self._cur_word, word
        for i, part in enumerate(word.parts):
            text = await self._expand_part(part, io_)
            self._cur_word = word
            if i == 0 and isinstance(part, Literal) and not part.quoted:
                tp = self._tilde_prefix(text)
                if tp:
                    text = tp[0] + text[tp[1]:]
            total += len(text)
            if total > self.max_string_bytes:
                raise self.too_large("expansion", "write the data to a file instead")
            out.append(text)
        self._cur_word = saved
        return "".join(out)

    async def _expand_assignment(self, word: Word, io_: _Io) -> str:
        """An assignment's value: one string, with ~ expanded at the start and after each ':' (PATH=~/bin:~/x)."""
        out: list[str] = []
        for i, part in enumerate(word.parts):
            self._cur_word = word
            text = await self._expand_part(part, io_)
            if isinstance(part, Literal) and not part.quoted and "~" in text:
                at_start = i == 0 or "".join(out).endswith(":")

                def tilde(m: re.Match) -> str:
                    if m.start() == 0 and not at_start:
                        return m.group(0)
                    v = self._tilde_value(m.group(1))
                    return m.group(0) if v is None else v

                text = _ASSIGNMENT_TILDE.sub(tilde, text)
            out.append(text)
        return "".join(out)

    async def _pattern(self, word: Word, io_: _Io) -> str:
        """Expands a word into a glob pattern: quoted text is escaped, unquoted text keeps its wildcards."""
        out = []
        for part in word.parts:
            text = await self._expand_part(part, io_)
            out.append(glob_escape(text) if part.quoted else text)
        return "".join(out)

    def _tilde_value(self, user: str) -> str | None:
        """~, ~user, ~+ and ~-: the directory they name, or None for an unknown user."""
        if user == "":
            return self.env.get("HOME", "/")
        if user == "+":
            return self._cwd
        if user == "-":
            return self.env.get("OLDPWD")
        if user == "root":
            return "/root"
        if user == self.user:
            return "/home/" + user
        return None

    def _tilde_prefix(self, text: str):
        """A leading ~ or ~user (up to the first '/'): (its directory, how many characters it spans)."""
        if not text.startswith("~"):
            return None
        end = text.find("/")
        if end < 0:
            end = len(text)
        home = self._tilde_value(text[1:end])
        return (home, end) if home is not None else None

    async def _alternative_word(self, expr: str, io_: _Io):
        """An unquoted ${p:-word} or ${p:+word} whose word applies: the word's parts, so quotes inside the word still keep
        it from being split (${v:-"a b"} is one field). None when p's own value applies."""
        element = array_ref(expr)
        match = _PARAM.match(expr) if element is None else None
        if element is not None and not element[0] and element[2] not in ("@", "*"):
            rest = element[3]
        elif match:
            rest = match.group(2)
        else:
            rest = ""
        d = default_op(rest)
        if d is None or d[0][-1] not in "-+":
            return None
        op, word = d
        if element is not None:
            value = self.get_element(element[1], await self._subscript(element[2], io_))
        else:
            value = self.get_variable(match.group(1)) if self._is_set(match.group(1)) else None
        missing = not value if op[0] == ":" else value is None
        if (not missing) if op[-1] == "-" else missing:
            return None if op[-1] == "-" else []
        # the word's unquoted text is split like any expansion's
        return [ExpandedText(p.text) if isinstance(p, Literal) and not p.quoted else p for p in self._param_word(word, False).parts]

    async def _expand_words(self, words, io_: _Io) -> list[str]:
        result: list[str] = []
        total = 0  # all the words together, so {1..10000}$big can't multiply a big value
        for word in [w for word in words for w in brace_expand(word)]:
            fields: list[_Field] = []
            cur = _Field()

            def end():
                nonlocal cur
                if cur.has_content:
                    fields.append(cur)
                cur = _Field()

            def add(s: str, quoted: bool):
                nonlocal total
                total += len(s)
                if total > self.max_string_bytes:
                    raise self.too_large("expansion", "write the data to a file instead")
                cur.text.append(s)
                cur.pattern.append(glob_escape(s) if quoted else s)
                if not quoted and has_wildcard(s):
                    cur.has_glob = True
                cur.has_content = True

            def add_split(text: str):
                # unquoted $VAR / $(cmd) / $((...)): split into fields on $IFS
                ifs = self.ifs
                pieces = split_fields(text, ifs)
                if not pieces:
                    if text and ifs:
                        end()
                    return
                if text[0] in ifs:
                    end()
                for k, piece in enumerate(pieces):
                    if k:
                        end()
                    add(piece, False)
                if text[-1] in ifs:
                    end()

            def add_each(items, quoted: bool):
                # "$@" / "${a[@]}": one field per item, and none at all when there are none; unquoted, each item is split
                for k, item in enumerate(items):
                    if k:
                        end()
                    if quoted:
                        add(item, True)
                    else:
                        add_split(item)

            parts = list(word.parts)
            i = 0
            while i < len(parts):
                part = parts[i]
                if isinstance(part, Variable):
                    name = self._indirect_target(part.name) or part.name
                    if name == "@" or (name == "*" and not part.quoted):
                        add_each(self.positional, part.quoted)
                        i += 1
                        continue
                    if len(name) > 1 and name[0] in "@*" and name[1] == ":" and default_op(name[1:]) is None:
                        # ${@:2} and ${@:2:3} slice the parameters, not the characters; ${@:0} starts at $0
                        spec = name[2:].split(":", 1)
                        all_ = ["bash"] + self.positional
                        frm = await self._arith(spec[0], io_)
                        if frm < 0:
                            frm += len(all_)
                        count = len(all_) if len(spec) == 1 else await self._arith(spec[1], io_)
                        items = [] if frm < 0 or frm > len(all_) else all_[frm:frm + max(0, min(count, len(all_)))]
                        if part.quoted and name[0] == "*":
                            add(self._ifs_joiner.join(items), True)
                        else:
                            add_each(items, part.quoted)
                        i += 1
                        continue
                    ar = array_ref(name)
                    if ar is not None and ar[2] in ("@", "*") and (ar[2] == "@" or not part.quoted):
                        elements = await self._array_elements(name, io_, part.quoted)
                        if elements is not None:
                            add_each(elements, part.quoted)
                            i += 1
                            continue
                    if not part.quoted and ("-" in name or "+" in name):
                        alternative = await self._alternative_word(name, io_)
                        if alternative is not None:
                            parts[i:i + 1] = alternative
                            continue
                    if name != part.name:
                        part = Variable(name, part.quoted)
                self._cur_word = word
                text = await self._expand_part(part, io_)
                if isinstance(part, Literal):
                    if i == 0 and not part.quoted:
                        tp = self._tilde_prefix(text)
                        if tp:
                            add(tp[0], True)  # the home path is never globbed
                            text = text[tp[1]:]
                            if not text:
                                i += 1
                                continue
                    add(text, part.quoted)
                elif part.quoted:
                    add(text, True)
                else:
                    add_split(text)
                i += 1
            end()

            for f in fields:
                glob = f.has_glob and not self.noglob
                matches = glob_expand(self, "".join(f.pattern)) if glob else []
                if matches:
                    result.extend(matches)
                elif not (glob and "nullglob" in self.shopts):
                    result.append("".join(f.text))
        return result


# ---------- helpers ----------

def _arith_nesting(expr: str) -> int:
    """How deeply $(( ... )) nest in expr (parentheses inside them are counted as they open and close)."""
    depth = deepest = 0
    stack: list[bool] = []
    for m in re.finditer(r"\$\(\(|\(|\)", expr):
        if m.group() == ")":
            if stack and stack.pop():
                depth -= 1
        else:
            if m.group() == "(":
                stack.append(False)
            else:  # $(( opens two parentheses; the outer one's close ends the arithmetic
                stack += [True, False]
                depth += 1
                deepest = max(deepest, depth)
    return deepest


def split_fields(text: str, ifs: str, max_fields: int = 0) -> list[str]:
    """Splits text on IFS like bash: IFS whitespace runs collapse and are trimmed at the ends, every other IFS
    character delimits one field. max_fields > 0 leaves the rest of the line in the last field (`read a b`)."""
    if not ifs:
        return [text] if text else []
    ws = "".join(c for c in " \t\n" if c in ifs)
    other = "".join(c for c in ifs if c not in ws)
    text = text.strip(ws) if ws else text
    if not text:
        return []
    ws_rx = f"[{re.escape(ws)}]" if ws else ""
    if not other:
        sep = ws_rx + "+"
    elif not ws:
        sep = f"[{re.escape(other)}]"
    else:
        sep = f"{ws_rx}*[{re.escape(other)}]{ws_rx}*|{ws_rx}+"
    fields = [text] if max_fields == 1 else _cached(sep, 0).split(text, maxsplit=max(0, max_fields - 1))
    if max_fields <= 0 and other and fields and fields[-1] == "":
        fields.pop()  # "a,b," is two fields, not three
    return fields


def _read_file_word(source: str):
    """The file word of `$(< file)`, or None when the substitution is anything else."""
    if "<" not in source:
        return None
    try:
        script = Parser.parse(source)
    except ParseError:
        return None
    if len(script.items) != 1 or script.items[0].rest or len(script.items[0].first.commands) != 1:
        return None
    cmd = script.items[0].first.commands[0]
    if isinstance(cmd, SimpleCommand) and not cmd.words and not cmd.assignments and len(cmd.redirects) == 1 \
            and cmd.redirects[0].op == "<" and not script.items[0].first.negate:
        return cmd.redirects[0].target
    return None


class _Field:
    def __init__(self):
        self.text: list[str] = []
        self.pattern: list[str] = []
        self.has_glob = False
        self.has_content = False
