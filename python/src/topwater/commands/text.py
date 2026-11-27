"""Text: grep, head, tail, wc, sort, uniq, cut, tr, tee, xargs, sed, nl, rev, tac, paste, comm, join, column, seq, diff. See docs/architecture.md."""
from __future__ import annotations

import re

from .. import bytetext
from .._spec import COMMANDS, GETOPT
from ..format import printf_format, unescape
from ..shell import CommandContext, TimedPattern, glob_to_regex
from ..vfs import ShellError, UsageException, normalize
from . import Args, _Errors, inputs, spec_format, spec_message, split_lines, write_lines
from .diff import _diff


_CLASSES = {
    "alpha": "a-zA-Z", "digit": "0-9", "alnum": "a-zA-Z0-9", "upper": "A-Z", "lower": "a-z", "space": r" \t\n\r\f\v",
    "blank": r" \t", "punct": r"!-/:-@\[-`{-~", "xdigit": "0-9A-Fa-f", "cntrl": r"\x00-\x1f\x7f", "print": r"\x20-\x7e",
    "graph": r"\x21-\x7e", "word": "a-zA-Z0-9_",
}


_ERE_RUN, _BRE_RUN = re.compile(r"[*+?]*"), re.compile(r"\**")


def posix_to_python(pattern: str, extended: bool, bracket_escapes: bool = False) -> str:
    r"""POSIX regex (grep/sed; basic unless extended) -> Python: [[:class:]] brackets, \< \> word edges, and in basic
    mode \( \) \{ \} \| \+ \? are the operators while bare ( ) { } | + ? are literals."""
    out: list[str] = []
    n = len(pattern)
    last: str | None = None  # last character emitted outside brackets, to spot a leading *
    groups: list[int] = []  # where each open group starts in out
    atom = 0  # where the last atom starts in out
    quantified: str | None = None  # the quantifier on the last atom, as emitted
    wraps = 0  # groups wrapped around the last atom

    def put_atom(text: str) -> None:
        nonlocal atom, quantified, wraps
        atom, quantified, wraps = len(out), None, 0
        out.append(text)

    def put_quantifier(text: str) -> None:
        nonlocal quantified, wraps
        if quantified is not None and len(quantified) == 1 and len(text) == 1:
            # a run of * + ? is one quantifier: ++ is +, ?? is ?, any other mix is *; linear however long the run
            quantified = out[-1] = text if quantified == text else "*"
            return
        if quantified is not None:
            # Python makes a+* possessive or an error; POSIX applies them in turn. Keep the wraps to a few.
            wraps += 1
            if wraps > 32:
                raise ShellError("Regular expression too big", 2)
            out.insert(atom, "(?:")
            out.append(")")
        quantified = text
        out.append(text)

    i = 0
    while i < n:
        c = pattern[i]
        if c == "[":
            j = i + 1
            body = []
            if j < n and pattern[j] == "^":
                body.append("^")
                j += 1
            if j < n and pattern[j] == "]":
                body.append(r"\]")
                j += 1
            while j < n and pattern[j] != "]":
                if pattern[j] == "[" and j + 1 < n and pattern[j + 1] in ":.=":
                    close = pattern.find(pattern[j + 1] + "]", j + 2)
                    if close >= 0:
                        name = pattern[j + 2:close]
                        if pattern[j + 1] == ":":
                            if name not in _CLASSES:
                                raise ShellError(f"invalid character class '{name}'", 2)
                            body.append(_CLASSES[name])
                        else:
                            body.append(re.escape(name))
                        j = close + 2
                        continue
                if bracket_escapes and pattern[j] == "\\" and j + 1 < n:  # awk: \/ \] \. are escapes inside brackets too
                    body.append(pattern[j:j + 2])
                    j += 2
                    continue
                body.append("\\" + pattern[j] if pattern[j] in "\\[" else pattern[j])  # backslash is literal in POSIX brackets
                j += 1
            if j >= n:
                put_atom(r"\[")
                last = "["
                i += 1
                continue
            put_atom("[" + "".join(body) + "]")
            last = "]"
            i = j + 1
            continue
        escaped = c == "\\" and i + 1 < n
        if escaped:
            i += 1
            c = pattern[i]
        op = escaped != extended and c in "(){}|+?"  # operators: bare in ERE, escaped in BRE
        leading = last is None or last in ("^", "(", "|")  # a quantifier here is a literal
        end = _interval_end(pattern, i + 1, extended) if op and c == "{" and not leading else None
        if escaped and c in "<>":
            put_atom(r"\b")
        elif op and c == "(":
            groups.append(len(out))
            out.append("(")
        elif op and c == ")" and groups:
            atom, quantified, wraps = groups.pop(), None, 0
            out.append(")")
        elif op and c == "|":
            out.append("|")
        elif ((c == "*" and not escaped) or (op and c in "+?")) and not leading:
            put_quantifier(c)
            # the rest of a run of bare * (and + ? in ERE) in one step: a pure-Python loop is slow on a long run
            j = (_ERE_RUN if extended else _BRE_RUN).match(pattern, i + 1).end()
            if j > i + 1:
                rest = pattern[i + 1:j]
                put_quantifier("*" if rest.strip(c) or c == "*" else c)
                i = j - 1
        elif end is not None:
            put_quantifier("{" + ("0" if pattern[i + 1] == "," else "") + pattern[i + 1:end] + "}")
            i = end + (0 if extended else 1)
        elif escaped and c not in "(){}|+?*":
            put_atom("\\" + c)  # \. \w \1 ...
        else:
            put_atom("\\" + c if c in "(){}|+?*" else c)  # a literal, or . ^ $ as they are
        last = c if op and c in "(|" else "x" if escaped or c in "(|" else c
        i += 1
    return "".join(out)


def _interval_end(p: str, i: int, extended: bool) -> int | None:
    """The end of an interval whose text starts at i (after the "{"): the index of its "}" (of "\\}" in basic mode), or
    None when the text is not m, m, m,n or ,n."""
    j = i
    while j < len(p) and p[j] in "0123456789,":
        j += 1
    body = p[i:j]
    if body in ("", ",") or body.count(",") > 1:
        return None
    return j if p.startswith("}" if extended else "\\}", j) else None


CLASS_SYNTAX = "character class syntax is [[:space:]], not [:space:]"


def posix_regex_error(p: str, extended: bool, grep: bool) -> str | None:
    r"""The error GNU grep/sed report for a malformed POSIX regex (basic unless extended), or None when it is valid.
    grep is lenient where sed is not: in grep -E a stray ")" and a "{" that does not start a valid interval are literals."""
    content, unmatched = r"Invalid content of \{\}", "Unmatched [, [^, [:, [., or [="
    n, open_, closed = len(p), 0, 0
    at_start = True  # an interval here has nothing to repeat
    i = 0
    while i < n:
        c = p[i]
        if c == "[":
            j = i + 1
            if j < n and p[j] == "^":
                j += 1
            if j >= n:
                return "Invalid regular expression"
            start = j
            if p[j] == "]":
                j += 1
            low: str | None = None  # the character a following '-' starts a range from
            while j < n and p[j] != "]":
                if p[j] == "[" and j + 1 < n and p[j + 1] in ":.=":
                    kind = p[j + 1]
                    close = p.find(kind + "]", j + 2)
                    if close < 0:
                        return unmatched
                    name = p[j + 2:close]
                    if kind == ":" and name not in _CLASSES:
                        return "Invalid character class name"
                    if kind == "." and len(name) != 1:
                        return "Invalid collation character"
                    low = name[0] if kind == "." else None
                    j = close + 2
                    continue
                if p[j] == "-" and low is not None and j + 1 < n and p[j + 1] != "]":
                    high, after = p[j + 1], j + 2
                    if p.startswith("[.", j + 1):  # a range may end in a collating symbol (not an equivalence class)
                        close = p.find(p[j + 2] + "]", j + 3)
                        if close < 0:
                            return unmatched
                        if len(p[j + 3:close]) != 1:
                            return "Invalid collation character"
                        high, after = p[j + 3], close + 2
                    if high < low:
                        return "Invalid range end"
                    low = None
                    j = after
                    continue
                low = p[j]
                j += 1
            if j >= n:
                return unmatched
            if j - start >= 2 and p[start] == ":" and p[j - 1] == ":":
                return CLASS_SYNTAX
            i = j + 1
            at_start = False
            continue
        escaped = c == "\\"
        if escaped:
            if i + 1 >= n:
                return "Trailing backslash"
            i += 1
            c = p[i]
        i += 1
        if escaped and "1" <= c <= "9":
            if int(c) > closed:
                return "Invalid back reference"
        elif escaped != extended and c == "(":
            open_ += 1
            at_start = True
            continue
        elif escaped != extended and c == ")":
            if open_ == 0:
                if extended and grep:
                    at_start = False
                    continue
                return r"Unmatched ) or \)"
            open_ -= 1
            closed += 1
        elif escaped != extended and c == "|":
            at_start = True
            continue
        elif escaped != extended and c == "{" and not at_start:
            # {m} {m,} {m,n} {,n}; in grep -E anything else that is not obviously an interval is a literal "{"
            lenient = extended and grep
            end = "}" if extended else r"\}"

            def num(k: int) -> tuple[int, int]:
                value = -1
                while k < n and p[k] != "," and not p.startswith(end, k):
                    value = -2 if value == -2 or not "0" <= p[k] <= "9" else min(100000, max(value, 0) * 10 + int(p[k]))
                    k += 1
                return value, k

            lo, k = num(i)
            if k >= n:
                if lenient:
                    continue
                return r"Unmatched \{"
            if lo == -2:
                if lenient:
                    continue
                return content
            if p[k] == ",":
                hi, k = num(k + 1)
                if k >= n:
                    if lenient:
                        continue
                    return r"Unmatched \{"
                if hi == -2:
                    if lenient:
                        continue
                    return content
                if p[k] == ",":
                    return content
                lo = max(lo, 0)
            elif lo < 0:
                return content
            else:
                hi = lo
            if hi >= 0 and lo > hi:
                return content
            if max(lo, hi) > 32767:
                return "Regular expression too big"
            i = k + len(end)
        at_start = False
    return r"Unmatched ( or \(" if open_ > 0 else None


def _regex(patterns: list[str], extended: bool, fixed: bool, ignore_case: bool, word: bool = False, line: bool = False,
           perl: bool = False) -> re.Pattern:
    def keep(p: str) -> str:
        # Python's re has no \K ("keep"): a leading X\KY means the same as (?<=X)Y
        k = p.find("\\K")
        return f"(?<={p[:k]}){p[k + 2:]}" if k > 0 else p

    # GNU reports every bad pattern, one line each; the shell adds the first "grep: "
    errors = [] if fixed or perl else [e for p in patterns if (e := posix_regex_error(p, extended, grep=True))]
    if errors:
        raise ShellError("\ngrep: ".join(errors), 2)
    parts = [re.escape(p) if fixed else keep(p) if perl else posix_to_python(p, extended) for p in patterns]
    rx = parts[0] if len(parts) == 1 else "|".join(f"(?:{p})" for p in parts)
    if word:
        rx = rf"\b(?:{rx})\b"
    if line:
        rx = rf"^(?:{rx})$"
    try:
        return TimedPattern(re.compile(rx, re.IGNORECASE if ignore_case else 0))
    except re.error as e:
        raise ShellError(spec_message("grep", "invalidRegex", detail=str(e)), 2) from None


def _parse_list(spec: str) -> tuple[set[int], float]:
    """"1,3-5,7-" -> ({1, 3, 4, 5}, 7): the 1-based indexes, plus "everything from" an open range (inf when none)."""
    result: set[int] = set()
    open_from = float("inf")
    for part in spec.split(","):
        a, dash, b = part.partition("-")
        if not re.fullmatch(r"\d*", a) or not re.fullmatch(r"\d*", b) or (not a and not b) or (a and int(a) == 0):
            raise ShellError(spec_message("cut", "fieldsFromOne") if a == "0" else spec_message("cut", "invalidFieldValue", part=part))
        if not dash:
            result.add(int(a))
        elif not b:
            open_from = min(open_from, int(a))
        else:
            result.update(range(int(a) if a else 1, int(b) + 1))
    return result, open_from


def _grep(ctx):
    # --include/--exclude/--exclude-dir and -e/-f may repeat, so pull them out before getopt-style parsing
    include, exclude, exclude_dir, rest = [], [], [], []
    patterns: list[str] = []
    args = list(ctx.args)
    i = 0
    while i < len(args):
        arg = args[i]
        i += 1
        if arg in ("--include", "--exclude", "--exclude-dir") and i < len(args):
            arg += "=" + args[i]
            i += 1
        for prefix, target in (("--include=", include), ("--exclude=", exclude), ("--exclude-dir=", exclude_dir)):
            if arg.startswith(prefix):
                target.append(glob_to_regex(arg[len(prefix):]))
                break
        else:
            m = re.fullmatch(r"-([a-zA-Z]*?)([ef])(.*)", arg, re.S) if not arg.startswith("--") else None
            long_ = re.fullmatch(r"--(regexp|file)(?:=(.*))?", arg, re.S)
            if m and set(m.group(1)) <= set("invcrRlLEFwxohHqsIaZP"):
                kind, value = m.group(2), m.group(3)
                if m.group(1):
                    rest.append("-" + m.group(1))
            elif long_:
                kind, value = long_.group(1)[0], long_.group(2) or ""
                if long_.group(2) is None and i < len(args):
                    value = args[i]
                    i += 1
                m = None
            else:
                rest.append(arg)
                continue
            if m is not None and value == "":
                if i >= len(args):
                    raise UsageException(spec_format(GETOPT["missingShortArgument"], option=kind))
                value = args[i]
                i += 1
            if kind == "e":
                patterns.append(value)
            else:
                patterns.extend(split_lines(ctx.fs.read_text(ctx.resolve(value))) if value != "-" else split_lines(ctx.stdin))
    a = Args.parse(rest, COMMANDS["grep"])
    positional = list(a.positional)
    if not patterns:
        if not positional:
            raise UsageException("")
        patterns = [positional.pop(0)]
    # a pattern with newlines is several patterns
    patterns = [p for pat in patterns for p in (pat.split("\n") if pat else [pat])]
    recursive = a.has("r") or a.has("R")
    implicit_dot = recursive and not positional
    if implicit_dot:
        positional = ["."]
    if a.has("E") and not a.has("F") and not a.has("P"):
        for p in patterns:
            if lead := re.search(r"^[*+?]|(?<!\\)[(|]([*+?])", p):
                ctx.error("grep: " + spec_message("grep", "leadingRepetition", char=lead.group(0)[-1]))
    rx = _regex(patterns, a.has("E"), a.has("F"), a.has("i"), a.has("w"), a.has("x"), a.has("P"))
    after = int(a.value("A") or a.value("C") or 0)
    before = int(a.value("B") or a.value("C") or 0)
    max_count = int(a.value("m")) if a.value("m") else 10**9

    files: list[str] = []
    errors = _Errors()
    walked = False
    for f in positional:
        path = ctx.resolve(f)
        if not ctx.fs.is_dir(path):
            files.append(f)
            continue
        walked = True
        if not recursive:
            if not a.has("s"):
                ctx.error("grep: " + spec_message("grep", "openFailed", file=f, errno="Is a directory"))
            errors()
            continue

        above = {ctx.fs.real_path(path)[0] or path}  # the directories being walked

        def walk(d, display):
            try:
                names = ctx.fs.listdir(d)
            except ShellError:
                if not a.has("s"):
                    ctx.error("grep: " + spec_message("grep", "openFailed", file=display, errno="Permission denied"))
                errors()
                return
            for name in names:
                ctx.check_time()  # -R can make the walk far bigger than the tree: the call's time limit
                child = normalize(d, name)
                child_display = name if display == "." and implicit_dot else display.rstrip("/") + "/" + name
                if not a.has("R") and ctx.fs.is_symlink(child):
                    continue  # -r skips links it meets; -R follows them
                if ctx.fs.is_dir(child):
                    if any(r.match(name) for r in exclude_dir):
                        continue
                    real = ctx.fs.real_path(child)[0]
                    if real is None or real in above:
                        if not a.has("s"):
                            ctx.error(f"grep: {child_display}: warning: recursive directory loop")
                        continue
                    above.add(real)
                    walk(child, child_display)
                    above.discard(real)
                elif (not include or any(r.match(name) for r in include)) and not any(r.match(name) for r in exclude):
                    files.append(child_display)

        walk(path, f)
    if positional and not files:
        return 2 if errors.hit else 1

    code = 1
    show_name = not a.has("h") and (a.has("H") or len(files) > 1 or walked)
    for name, text in inputs(ctx, files, errors, quiet=a.has("s")):
        if name == "(standard input)" and a.value("\x07") is not None:
            name = a.value("\x07")  # --label
        binary = "\0" in text and not a.has("a")  # GNU grep's test: a NUL byte
        if binary and a.has("I"):
            continue
        lines = split_lines(text)
        hits = []
        for i, line in enumerate(lines):
            if len(hits) >= max_count:
                break
            ctx.check_time()  # one slow match per line can't add up past the time limit
            if bool(rx.search(line)) != a.has("v"):
                hits.append(i)
        if hits:
            code = 0
        if a.has("q"):
            if code == 0:
                return 0
            continue
        prefix = name if show_name else None
        if a.has("l"):
            if hits:
                ctx.print(name)
            continue
        if a.has("L"):
            if not hits:
                ctx.print(name)
            continue
        if a.has("c"):
            ctx.print(f"{prefix}:{len(hits)}" if prefix is not None else len(hits))
            continue
        if binary:
            if hits:
                ctx.print("grep: " + spec_message("grep", "binaryFileMatches", file=name))
            continue
        hit_set = set(hits)
        last_printed = -1
        for h in hits:
            start = max(h - before, 0, last_printed + 1)
            stop = min(len(lines) - 1, h + after)
            if (before or after) and last_printed >= 0 and start > last_printed + 1:
                ctx.print("--")
            for i in range(start, stop + 1):
                sep = ":" if i in hit_set else "-"
                head = (prefix + sep if prefix is not None else "") + (f"{i + 1}{sep}" if a.has("n") else "")
                if a.has("o"):
                    if i in hit_set:
                        for m in rx.finditer(lines[i]):
                            if m.group(0):
                                ctx.print(head + m.group(0))
                else:
                    ctx.print(head + lines[i])
                last_printed = i
    return 2 if errors.hit and not (a.has("q") and code == 0) else code


def _head_tail(ctx):
    head = ctx.name == "head"
    # shortcut: tail -f/-F print what is there and return instead of following (nothing else writes the file)
    a = Args.parse(Args.numeric_shorthand(ctx.args), COMMANDS[ctx.name])
    if a.value("n") is not None:  # a bad count is reported before any file is opened
        _count_arg(ctx.name, a.value("n"), "lines")
    if a.value("c") is not None:
        _count_arg(ctx.name, a.value("c"), "bytes")
    errors = _Errors()
    items = list(inputs(ctx, a.positional, errors, raw=a.value("c") is not None))
    for k, (name, text) in enumerate(items):
        if (len(items) > 1 and not a.has("q")) or a.has("v"):
            ctx.print(("\n" if k else "") + f"==> {name} <==")
        if a.value("c") is not None:  # byte counts; text is bytes here
            spec = a.value("c")
            n = _count_arg(ctx.name, spec, "bytes")
            if head:
                chunk = text[: max(0, len(text) - n)] if spec.startswith("-") else text[:n]
            else:
                chunk = text[max(0, n - 1):] if spec.startswith("+") else text[max(0, len(text) - n):] if n else b""
            ctx.stdout.write(bytetext.decode(chunk))
            continue
        spec = a.value("n") or "10"
        n = _count_arg(ctx.name, spec, "lines")
        lines = split_lines(text)
        if head:
            pick = lines[: max(0, len(lines) - n)] if spec.startswith("-") else lines[:n]
        elif spec.startswith("+"):
            pick = lines[max(0, n - 1):]
        else:
            pick = lines[-n:] if n else []
        out = "".join(line + "\n" for line in pick)
        if pick and not text.endswith("\n") and (not head or len(pick) == len(lines)):
            out = out[:-1]  # the file's last line had no newline, and neither does the output
        ctx.stdout.write(out)
    return 1 if errors.hit else 0


def _count_arg(cmd: str, spec: str, what: str) -> int:
    try:
        return int(spec.lstrip("+-") or "x")
    except ValueError:
        raise ShellError(spec_message(cmd, "invalidCount", what=what, value=spec)) from None


def _wc(ctx):
    a = Args.parse(ctx.args, COMMANDS["wc"])
    every = not (a.flags & set("lwcm"))
    errors = _Errors()
    items = list(inputs(ctx, a.positional, errors, raw=True))
    totals = [0, 0, 0]

    texts = [data.decode("utf-8", "replace") for _, data in items]
    counts = [(t.count("\n"), len(t.split()), len(t) if a.has("m") else len(data)) for t, (_, data) in zip(texts, items)]
    selected = [every or a.has("l"), every or a.has("w"), every or a.has("c") or a.has("m")]
    # GNU pads every column to one width: none for a single count of a single file, 7 for stdin, else the total size
    if sum(selected) == 1 and len(a.positional) <= 1:
        width = 1
    elif any(name == "(standard input)" for name, _ in items):
        width = 7
    else:
        width = len(str(sum(len(data) for _, data in items)))

    def row(l, w, c, name):
        parts = [str(v).rjust(width) for v, on in zip((l, w, c), selected) if on]
        if name is not None:
            parts.append(name)
        return " ".join(parts)

    for (name, _), (l, w, c) in zip(items, counts):
        totals = [totals[0] + l, totals[1] + w, totals[2] + c]
        ctx.print(row(l, w, c, name if a.positional else None))
    if len(a.positional) > 1:
        ctx.print(row(*totals, "total"))
    return 1 if errors.hit else 0


_SORT_ORDERING = set("bdfghnrMV")


_HUMAN_SUFFIX = "KMGTPEZY"


def _sort_number(s: str, mode: str) -> float:
    s = s.strip()
    if mode == "g":
        m = re.match(r"[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?", s)
    else:
        m = re.match(r"-?(\d+\.?\d*|\.\d+)", s)
    if not m:
        return float("-inf") if mode == "g" else 0.0
    v = float(m.group(0))
    if mode == "h":
        suffix = s[m.end():m.end() + 1].upper()
        if suffix and suffix in _HUMAN_SUFFIX:
            v *= 1024.0 ** (_HUMAN_SUFFIX.index(suffix) + 1)
    return v


def _version_key(s: str) -> list:
    return [(0, int(p), "") if p.isdigit() else (1, 0, p) for p in re.findall(r"\d+|\D+", s)]


def _sort(ctx):
    import functools

    a = Args.parse(ctx.args, COMMANDS["sort"])
    keys_spec = [ctx.args[i + 1] for i in range(len(ctx.args) - 1) if ctx.args[i] in ("-k", "--key")]
    keys_spec += [x[2:] for x in ctx.args if x.startswith("-k") and len(x) > 2]
    keys_spec += [x[6:] for x in ctx.args if x.startswith("--key=")]
    sep = a.value("t")
    if sep is not None and len(unescape(sep)) != 1:
        raise ShellError(spec_message("sort", "multiCharacterTab", value=sep), 2)
    sep = unescape(sep) if sep is not None else None
    global_opts = "".join(c for c in "bdfghnrMV" if a.has(c))
    keys = []  # (start field, start char, end field, end char, options)
    for spec in keys_spec:
        km = re.fullmatch(r"(\d+)(?:\.(\d+))?([a-zA-Z]*)(?:,(\d+)(?:\.(\d+))?([a-zA-Z]*))?", spec)
        if not km or int(km.group(1)) == 0:
            raise ShellError(spec_message("sort", "invalidKey", value=spec), 2)
        opts = km.group(3) + (km.group(6) or "")
        keys.append((int(km.group(1)), int(km.group(2) or 1), int(km.group(4)) if km.group(4) else None,
                     int(km.group(5) or 0), opts if set(opts) & _SORT_ORDERING else global_opts + opts))
    if not keys:
        keys.append((0, 1, None, 0, global_opts))

    def extract(line: str, k) -> str:
        sf, sc, ef, ec, _ = k
        if sf == 0:
            return line
        fields = line.split(sep) if sep is not None else line.split()
        joiner = sep if sep is not None else " "
        if sf > len(fields):
            return ""
        end = len(fields) if ef is None else min(ef, len(fields))
        chosen = fields[sf - 1:end]
        if not chosen:
            return ""
        chosen[0] = chosen[0][sc - 1:]
        if ef is not None and ec and ef <= len(fields) and len(chosen) == end - sf + 1:
            chosen[-1] = chosen[-1][:ec - (sc - 1 if len(chosen) == 1 else 0)]
        return joiner.join(chosen)

    def key_value(text: str, opts: str):
        if "b" in opts:
            text = text.lstrip()
        if "d" in opts:
            text = "".join(c for c in text if c.isalnum() or c.isspace())
        if "f" in opts:
            text = text.upper()
        for mode in "nghV":
            if mode in opts:
                return _version_key(text) if mode == "V" else _sort_number(text, mode)
        if "M" in opts:
            months = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"]
            m = text.strip()[:3].upper()
            return months.index(m) + 1 if m in months else 0
        return text

    def cmp(x, y):
        for (vx, vy, k) in zip(x[1], y[1], keys):
            if vx != vy:
                c = -1 if vx < vy else 1
                return -c if "r" in k[4] else c
        if a.has("s") or a.has("u"):
            return 0
        c = (x[0] > y[0]) - (x[0] < y[0])
        return -c if a.has("r") else c

    errors = _Errors()
    named = []
    for f in a.positional or ["-"]:
        named += inputs(ctx, [f], errors)
        if errors.hit:
            return 2  # sort stops at the first file it can't read
    lines = [line for _, text in named for line in split_lines(text)]
    items = [(line, [key_value(extract(line, k), k[4]) for k in keys]) for line in lines]
    if a.has("c") or a.has("C"):
        for i in range(1, len(items)):
            if cmp(items[i - 1], items[i]) > 0 or (a.has("u") and cmp(items[i - 1], items[i]) == 0):
                if a.has("c"):
                    ctx.error("sort: " + spec_message("sort", "disorder", file=named[0][0] if a.positional else "-", line=str(i + 1), text=items[i][0]))
                return 1
        return 0
    items.sort(key=functools.cmp_to_key(cmp))
    if a.has("u"):
        unique = []
        for it in items:
            if not unique or cmp(unique[-1], it) != 0:
                unique.append(it)
        items = unique
    output = "".join(it[0] + "\n" for it in items)
    if a.value("o"):
        ctx.fs.write_text(ctx.resolve(a.value("o")), output)
    else:
        ctx.stdout.write(output)
    return 2 if errors.hit else 0


def _uniq(ctx):
    a = Args.parse(ctx.args, COMMANDS["uniq"])
    errors = _Errors()
    text = "".join(t for _, t in inputs(ctx, a.positional[:1], errors))
    if errors.hit:
        return 1
    groups: list[list] = []
    for line in split_lines(text):
        same = groups and (groups[-1][0].lower() == line.lower() if a.has("i") else groups[-1][0] == line)
        if same:
            groups[-1][1] += 1
        else:
            groups.append([line, 1])
    for line, count in groups:
        if (a.has("d") and count < 2) or (a.has("u") and count > 1):
            continue
        ctx.print(f"{count:7} {line}" if a.has("c") else line)


def _cut(ctx):
    a = Args.parse(ctx.args, COMMANDS["cut"])
    delim = a.value("d") if a.value("d") is not None else "\t"
    if len(delim) != 1:
        raise ShellError(spec_message("cut", "delimiterNotSingle"))
    chars = a.value("c") or a.value("b")
    if not chars and not a.value("f"):
        raise UsageException(spec_message("cut", "listRequired"))
    wanted, open_from = _parse_list(chars or a.value("f"))
    complement = a.has("z")

    def pick(items: list) -> list:
        return [x for i, x in enumerate(items, 1) if (i >= open_from or i in wanted) != complement]

    out_delim = a.value("\x02")  # --output-delimiter (long only)
    errors = _Errors()
    out = []
    for _, text in inputs(ctx, a.positional, errors):
        for line in split_lines(text):
            if chars:
                out.append(("" if out_delim is None else out_delim).join(pick(list(line))) + "\n")
                continue
            if delim not in line:
                if not a.has("s"):
                    out.append(line + "\n")
                continue
            out.append((delim if out_delim is None else out_delim).join(pick(line.split(delim))) + "\n")
    ctx.stdout.write("".join(out))
    return 1 if errors.hit else 0


_TR_CLASSES = {
    "upper": "A-Z", "lower": "a-z", "digit": "0-9", "alpha": "a-zA-Z", "alnum": "a-zA-Z0-9", "space": " \t\n\r\f\v",
    "blank": " \t", "xdigit": "0-9A-Fa-f", "punct": "!-/:-@[-`{-~", "cntrl": "\x00-\x1f\x7f", "print": " -~", "graph": "!-~",
}


def _expand_set(spec: str) -> str:
    spec = re.sub(r"\[:(\w+):\]", lambda m: _TR_CLASSES.get(m.group(1), m.group(0)), spec)
    spec = unescape(spec)
    out = []
    i = 0
    while i < len(spec):
        if i + 2 < len(spec) and spec[i + 1] == "-" and spec[i] <= spec[i + 2]:
            out.extend(chr(c) for c in range(ord(spec[i]), ord(spec[i + 2]) + 1))
            i += 3
        else:
            out.append(spec[i])
            i += 1
    return "".join(out)


def _tr(ctx):
    a = Args.parse(ctx.args, COMMANDS["tr"])
    if not a.positional:
        raise UsageException(spec_message("tr", "missingOperand"))
    if len(a.positional) > 2:
        raise UsageException(spec_message("tr", "extraOperand", operand=a.positional[2]))
    set1 = _expand_set(a.positional[0])
    set2 = _expand_set(a.positional[1]) if len(a.positional) > 1 else ""
    complement = a.has("c") or a.has("C")
    in_set1 = (lambda ch: ch not in set1) if complement else (lambda ch: ch in set1)
    translate = bool(set2) and not a.has("d")
    squeeze = (lambda ch: ch in set2) if set2 else in_set1  # -s applies to the last set given
    if not complement and not a.has("s") and (a.has("d") or set2):
        # fast path: one str.translate pass instead of a Python loop per character
        table: dict[int, str | None] = {}
        for i, ch in enumerate(set1):
            table.setdefault(ord(ch), None if a.has("d") else set2[min(i, len(set2) - 1)])
        ctx.stdout.write(ctx.stdin.translate(table))
        return
    out: list[str] = []
    for ch in ctx.stdin:
        hit = in_set1(ch)
        if a.has("d") and hit:
            continue
        o = ch
        if translate and hit:
            o = set2[-1] if complement else set2[min(set1.index(ch), len(set2) - 1)]
        if a.has("s") and out and out[-1] == o and squeeze(o):
            continue
        out.append(o)
    ctx.stdout.write("".join(out))


def _tee(ctx):
    a = Args.parse(ctx.args, COMMANDS["tee"])
    code = 0
    for f in a.positional:
        path = ctx.resolve(f)
        if path in ("/dev/stdout", "/dev/stderr"):
            (ctx.stderr if path == "/dev/stderr" else ctx.stdout).write(ctx.stdin)
            continue
        try:
            (ctx.fs.append_text if a.has("a") else ctx.fs.write_text)(path, ctx.stdin)
        except ShellError as e:
            ctx.error("tee: " + spec_message("tee", "writeFailed", file=f, reason=str(e).rsplit(": ", 1)[-1]))
            code = 1
    ctx.stdout.write(ctx.stdin)
    return code


def _xargs_words(text: str) -> list[str]:
    """Splits xargs input like GNU xargs: blanks separate items; quotes and backslashes protect them."""
    items: list[str] = []
    cur: list[str] = []
    in_word = False
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c in " \t\n":
            if in_word:
                items.append("".join(cur))
                cur, in_word = [], False
            i += 1
        elif c in "'\"":
            j = text.find(c, i + 1)
            if j < 0 or "\n" in text[i + 1:j]:
                kind = "single" if c == "'" else "double"
                raise ShellError(spec_message("xargs", "unmatchedQuote", kind=kind))
            cur.append(text[i + 1:j])
            in_word, i = True, j + 1
        elif c == "\\" and i + 1 < n:
            cur.append(text[i + 1])
            in_word, i = True, i + 2
        else:
            cur.append(c)
            in_word, i = True, i + 1
    if in_word:
        items.append("".join(cur))
    return items


async def _xargs(ctx):
    opts = []
    i = 0
    args = ctx.args
    while i < len(args) and args[i].startswith("-") and args[i] != "-":
        opts.append(args[i])
        if args[i] in ("-I", "-n", "-d", "-L", "-P", "-s", "-E", "-a") and i + 1 < len(args):
            i += 1
            opts.append(args[i])
        i += 1
    a = Args.parse(opts, COMMANDS["xargs"])
    command = list(args[i:]) or ["echo"]
    data = ctx.fs.read_text(ctx.resolve(a.value("a"))) if a.value("a") else ctx.stdin
    # shortcut: -P is accepted but commands run one after another (output order stays deterministic).
    if a.value("I") is not None or a.value("L") is not None:
        lines = [line.lstrip(" \t") if a.value("I") is not None else line for line in split_lines(data)]
        if a.value("I") is not None:
            groups = [[line] for line in lines if line]
        else:
            per = int(a.value("L"))
            if per < 1:
                raise ShellError(spec_message("xargs", "invalidValue", value=a.value("L"), option="L"))
            words = [_xargs_words(line) for line in lines]
            words = [w for w in words if w]
            groups = [[x for w in words[k:k + per] for x in w] for k in range(0, len(words), per)]
    else:
        if a.has("0"):
            items = [x for x in data.split("\0") if x]
        elif a.value("d") is not None:
            items = data.split(unescape(a.value("d"))[:1] or "\n")
            if items and items[-1] in ("", "\n"):
                items.pop()
        else:
            items = _xargs_words(data)
        per = int(a.value("n")) if a.value("n") else max(1, len(items))
        if per < 1:
            raise ShellError(spec_message("xargs", "invalidValue", value=a.value("n"), option="n"))
        groups = [items[k:k + per] for k in range(0, len(items), per)]
    if not groups:
        if a.has("r") or a.value("I") is not None:
            return 0
        groups = [[]]  # like GNU xargs, run the command once even with no input
    code = 0
    for group in groups:
        if a.value("I") is not None:
            argv = [c.replace(a.value("I"), group[0]) for c in command]
        else:
            argv = command + group
        if a.has("t"):
            ctx.stderr.write(" ".join(argv) + "\n")
        from . import exec_failure
        failure = exec_failure(ctx.shell, argv[0])
        if failure is not None:
            ctx.error("xargs: " + spec_message("xargs", "cannotRun", command=argv[0], errno=failure[0]))
            return failure[1]
        rc = await ctx.invoke(argv)
        if rc == 127:
            return 127
        if rc == 255:
            ctx.error("xargs: " + spec_message("xargs", "exitedWith255", command=argv[0]))
            return 124
        if rc != 0:
            code = 123
    return code


def _two_inputs(ctx, files: list[str], sep: str = "\n"):
    """comm and join: the lines of exactly two files (- is stdin), or None after reporting an unreadable one."""
    if len(files) < 2:
        raise UsageException(spec_message(ctx.name, "missingOperandAfter", operand=files[0]) if files else spec_message(ctx.name, "missingOperand"))
    if len(files) > 2:
        raise UsageException(spec_message(ctx.name, "extraOperand", operand=files[2]))
    errors = _Errors()
    out = []
    for f in files:
        got = list(inputs(ctx, [f], errors))
        if not got:
            return None
        lines = got[0][1].split(sep)
        if lines and lines[-1] == "":
            lines.pop()
        out.append(lines)
    return out


def _nl(ctx):
    a = Args.parse(ctx.args, COMMANDS["nl"])
    every = a.value("b") == "a"
    width = int(a.value("w") or 6)
    sep = a.value("s") or "\t"
    errors, n = _Errors(), 0
    for _, text in inputs(ctx, a.positional, errors):
        for line in split_lines(text):
            if not line and not every:
                ctx.print("")
                continue
            n += 1
            ctx.print(f"{str(n).rjust(width)}{sep}{line}")
    return 1 if errors.hit else 0


def _rev(ctx):
    errors = _Errors()
    for _, text in inputs(ctx, ctx.args, errors):
        for line in split_lines(text):
            ctx.print(line[::-1])
    return 1 if errors.hit else 0


def _tac(ctx):
    errors = _Errors()
    for _, text in inputs(ctx, ctx.args, errors):
        write_lines(ctx.stdout, reversed(split_lines(text)))
    return 1 if errors.hit else 0


def _paste(ctx):
    a = Args.parse(ctx.args, COMMANDS["paste"])
    delims = unescape(a.value("d") if a.value("d") is not None else "\t") or "\0"

    def join(cells):
        out = []
        for i, cell in enumerate(cells):
            if i and delims[(i - 1) % len(delims)] != "\0":
                out.append(delims[(i - 1) % len(delims)])
            out.append(cell)
        return "".join(out)

    errors = _Errors()
    if a.positional.count("-") > 1 and not a.has("s"):
        # several "-" operands take turns reading lines from stdin (seq 6 | paste - - -)
        stdin_lines = split_lines(ctx.stdin)
        n = a.positional.count("-")
        columns, k = [], 0
        for f in a.positional:
            if f == "-":
                columns.append(stdin_lines[k::n])
                k += 1
            else:
                columns.extend(split_lines(t) for _, t in inputs(ctx, [f], errors))
    else:
        columns = [split_lines(t) for _, t in inputs(ctx, a.positional, errors)]
    if a.has("s"):
        for col in columns:
            ctx.print(join(col))
    else:
        for r in range(max((len(c) for c in columns), default=0)):
            ctx.print(join([c[r] if r < len(c) else "" for c in columns]))
    return 1 if errors.hit else 0


def _comm_cmd(ctx):
    a = Args.parse(ctx.args, COMMANDS["comm"])
    sep = "\0" if a.has("z") else "\n"
    both = _two_inputs(ctx, a.positional, sep)
    if both is None:
        return 1
    left, right = both
    delim = a.value("\x03") if a.value("\x03") is not None else "\t"
    show = [not a.has("1"), not a.has("2"), not a.has("3")]
    indent = ["", delim * show[0], delim * (show[0] + show[1])]
    check, strict = not a.has("\x02"), a.has("\x01")
    out, counts, last, unsorted = [], [0, 0, 0], [None, None], [False, False]

    def flush():
        ctx.stdout.write("".join(x + sep for x in out))
        out.clear()

    def disorder(k: int, line: str) -> bool:
        """Notes line as file k's latest; True the first time file k is found out of order."""
        bad = check and last[k] is not None and line < last[k] and not unsorted[k]
        unsorted[k] |= bad
        last[k] = line
        return bad
    i = j = 0
    while i < len(left) or j < len(right):
        ctx.check_time()
        if j >= len(right) or (i < len(left) and left[i] < right[j]):
            col, line, bad = 0, left[i], [k for k in (0,) if disorder(0, left[i])]
            i += 1
        elif i >= len(left) or right[j] < left[i]:
            col, line, bad = 1, right[j], [k for k in (1,) if disorder(1, right[j])]
            j += 1
        else:
            col, line = 2, left[i]
            bad = [k for k, x in ((0, left[i]), (1, right[j])) if disorder(k, x)]
            i, j = i + 1, j + 1
        if bad and strict:  # --check-order stops before the line out of order
            flush()
            ctx.error("comm: " + spec_message("comm", "notSorted", n=str(bad[0] + 1)))
            return 1
        counts[col] += 1
        if show[col]:
            out.append(indent[col] + line)
        for k in bad:  # otherwise the line goes out first, then the warning
            flush()
            ctx.error("comm: " + spec_message("comm", "notSorted", n=str(k + 1)))
    if a.has("\x04"):
        out.append(delim.join(str(c) for c in counts) + delim + spec_message("comm", "total"))
    flush()
    if any(unsorted):
        ctx.error("comm: " + spec_message("comm", "inputNotSorted"))
        return 1
    return 0


def _join_cmd(ctx):
    a = Args.parse(ctx.args, COMMANDS["join"])
    tab = a.value("t") or None  # -t "" splits on blanks, as GNU join does
    if tab is not None and len(tab) > 1:
        raise ShellError(spec_message("join", "multiCharTab", value=tab))

    def field_no(v: str) -> int:
        if not v.isascii() or not v.isdigit() or int(v) < 1:
            raise ShellError(spec_message("join", "badField", value=v))
        return int(v) - 1
    k1 = k2 = 0
    if a.value("j") is not None:
        k1 = k2 = field_no(a.value("j"))
    if a.value("1") is not None:
        k1 = field_no(a.value("1"))
    if a.value("2") is not None:
        k2 = field_no(a.value("2"))
    unpaired = set()
    for v in a.all("a") + a.all("v"):
        if v not in ("1", "2"):
            raise ShellError(spec_message("join", "badFileNumberArg", value=v))
        unpaired.add(int(v))
    only = {int(v) for v in a.all("v")}
    fmt = None
    if a.value("o") is not None and a.value("o") != "auto":
        fmt = []
        for spec in re.split(r"[ ,]+", a.value("o").strip()):
            m = re.fullmatch(r"([0-9]+)\.([0-9]+)", spec)
            if spec == "0":
                fmt.append((0, 0))
            elif not m or m.group(1) not in ("1", "2"):
                raise ShellError(spec_message("join", "badFileNumber", spec=spec))
            else:
                fmt.append((int(m.group(1)), field_no(m.group(2))))
    both = _two_inputs(ctx, a.positional)
    if both is None:
        return 1

    def fields(line: str) -> list[str]:
        if tab is not None:
            return line.split(tab)
        line = line.strip(" \t")  # GNU join's blanks: spaces and tabs only
        return re.split(r"[ \t]+", line) if line else []
    left, right = [[fields(x) for x in side] for side in both]
    keys = (k1, k2)
    fold = a.has("i")

    def key(f: list[str], side: int) -> str:
        k = f[keys[side]] if keys[side] < len(f) else ""
        return k.lower() if fold else k
    if a.value("o") == "auto":
        fmt = [(0, 0)] + [(1, i) for i in range(len(left[0]) if left else 0) if i != k1] + \
              [(2, i) for i in range(len(right[0]) if right else 0) if i != k2]
    out_sep = tab if tab is not None else " "
    empty = a.value("e") if a.value("e") is not None else ""
    lines = []

    def emit(f1, f2) -> None:
        if fmt is not None:
            row = []
            for side, n in fmt:
                f = f1 if side == 1 else f2
                if side == 0:
                    src, k = (f1, k1) if f1 is not None else (f2, k2)
                    row.append(src[k] if k < len(src) else empty)
                else:
                    row.append(f[n] if f is not None and n < len(f) else empty)
        else:
            src, k = (f1, k1) if f1 is not None else (f2, k2)
            row = [src[k] if k < len(src) else ""]
            row += [x for i, x in enumerate(f1 or []) if i != k1] + [x for i, x in enumerate(f2 or []) if i != k2]
        lines.append(out_sep.join(row))
        if len(lines) >= 1024:  # written as it goes: one repeated key can pair up into far more rows than fit in memory
            ctx.check_time()
            ctx.stdout.write("".join(x + "\n" for x in lines))
            lines.clear()
    i = j = 0
    if a.has("\x01") and (left or right):
        emit(left[0] if left else None, right[0] if right else None)
        i = j = 1
    while i < len(left) or j < len(right):
        ctx.check_time()
        a_key = key(left[i], 0) if i < len(left) else None
        b_key = key(right[j], 1) if j < len(right) else None
        if b_key is None or (a_key is not None and a_key < b_key):
            if 1 in unpaired:
                emit(left[i], None)
            i += 1
        elif a_key is None or b_key < a_key:
            if 2 in unpaired:
                emit(None, right[j])
            j += 1
        else:
            i2, j2 = i, j
            while i2 < len(left) and key(left[i2], 0) == a_key:
                i2 += 1
            while j2 < len(right) and key(right[j2], 1) == a_key:
                j2 += 1
            if not only:
                for x in left[i:i2]:
                    for y in right[j:j2]:
                        emit(x, y)
            i, j = i2, j2
    ctx.stdout.write("".join(x + "\n" for x in lines))
    return 0


def _column(ctx):
    a = Args.parse(ctx.args, COMMANDS["column"])
    errors = _Errors()
    lines = [l for _, t in inputs(ctx, a.positional, errors) for l in split_lines(t)]
    if not a.has("t"):
        write_lines(ctx.stdout, lines)
        return 1 if errors.hit else 0
    sep = a.value("s")
    rows = []
    for l in lines:
        if not l.strip():
            continue
        rows.append([c for c in re.split("[" + re.escape(unescape(sep)) + "]", l) if c] if sep else l.split())
    out_sep = a.value("o") or "  "
    widths = [max((len(r[c]) for r in rows if c < len(r)), default=0) for c in range(max((len(r) for r in rows), default=0))]
    for r in rows:
        ctx.print(out_sep.join(cell if i == len(r) - 1 else cell.ljust(widths[i]) for i, cell in enumerate(r)))
    return 1 if errors.hit else 0


def _seq(ctx):
    nums, sep, equal_width, fmt = [], "\n", False, None
    args = ctx.args
    i = 0
    while i < len(args):
        x = args[i]
        if re.fullmatch(r"[-+]?(\d+\.?\d*|\.\d+)([eE][-+]?\d+)?", x):
            nums.append(x)
        elif x in ("-w", "--equal-width"):
            equal_width = True
        elif x == "-s":
            if i + 1 >= len(args):
                raise ShellError(spec_message("seq", "optionRequiresArgument", option="s"))
            i += 1
            sep = args[i]
        elif x.startswith("-s"):
            sep = x[2:]
        elif x.startswith("--separator="):
            sep = x[12:]
        elif x in ("-f", "--format") or x.startswith(("-f", "--format=")):
            if x in ("-f", "--format"):
                if i + 1 >= len(args):
                    raise ShellError(spec_message("seq", "optionRequiresArgument", option="f"))
                i += 1
                fmt = args[i]
            else:
                fmt = x[2:] if x.startswith("-f") else x[9:]
        else:
            raise ShellError(spec_message("seq", "invalidOption", option=x.lstrip("-")))
        i += 1
    if not nums or len(nums) > 3:
        raise UsageException(spec_message("seq", "missingOperand") if not nums else spec_message("seq", "extraOperand", operand=nums[3]))
    v = [float(x) for x in nums]
    first = v[0] if len(v) > 1 else 1
    step = v[1] if len(v) == 3 else 1
    last = v[-1]
    if step == 0:
        raise ShellError(spec_message("seq", "zeroIncrement"))
    decimals = max((len(x) - x.index(".") - 1 if "." in x else 0) for x in nums)
    items, k = [], 0
    while True:
        x = first + k * step  # not a running sum, so 0.1 steps don't drift
        if not ((x <= last + 1e-9) if step > 0 else (x >= last - 1e-9)):
            break
        items.append(printf_format(fmt, [repr(x)]) if fmt else f"{x:.{decimals}f}")
        if len(items) > 1_000_000:
            raise ShellError(spec_message("seq", "tooManyNumbers"))
        k += 1
    if equal_width and items:
        w = max(len(s) for s in items)
        items = [s.zfill(w) for s in items]
    if items:
        ctx.stdout.write(unescape(sep).join(items) + "\n")


def register(shell) -> None:
    from .sed import _sed  # here, not at the top: sed.py imports this module
    r = shell.register_command
    r("grep", _grep)
    # Ubuntu's egrep and fgrep are scripts that run `grep -E` / `grep -F`, so messages say "grep:"
    for alias, flag in (("egrep", "-E"), ("fgrep", "-F")):
        r(alias, lambda ctx, flag=flag: _grep(CommandContext(ctx.shell, "grep", [flag, *ctx.args], ctx._stdin, ctx.stdout, ctx.stderr)))
    r("head", _head_tail)
    r("tail", _head_tail)
    r("wc", _wc)
    r("sort", _sort)
    r("uniq", _uniq)
    r("cut", _cut)
    r("tr", _tr)
    r("tee", _tee)
    r("xargs", _xargs)
    r("sed", _sed)
    r("nl", _nl)
    r("rev", _rev)
    r("tac", _tac)
    r("paste", _paste)
    r("comm", _comm_cmd)
    r("join", _join_cmd)
    r("column", _column)
    r("seq", _seq)
    r("diff", _diff)
