"""Text tools: grep head tail wc sort uniq cut tr tee xargs sed."""
from __future__ import annotations

import io
import re

from .. import bytetext
from ..shell import CommandContext, glob_to_regex
from ..vfs import ShellError, UsageException, normalize
from . import Args, inputs, quote_l, split_lines, unescape


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
                    if p[j + 1] < low:
                        return "Invalid range end"
                    low = None
                    j += 2
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
        return re.compile(rx, re.IGNORECASE if ignore_case else 0)
    except re.error as e:
        raise ShellError(f"invalid regular expression: {e}", 2) from None


def _parse_list(spec: str) -> tuple[set[int], float]:
    """"1,3-5,7-" -> ({1, 3, 4, 5}, 7): the 1-based indexes, plus "everything from" an open range (inf when none)."""
    result: set[int] = set()
    open_from = float("inf")
    for part in spec.split(","):
        a, dash, b = part.partition("-")
        if not re.fullmatch(r"\d*", a) or not re.fullmatch(r"\d*", b) or (not a and not b) or (a and int(a) == 0):
            raise ShellError("fields and positions are numbered from 1" if a == "0" else f"invalid field value '{part}'")
        if not dash:
            result.add(int(a))
        elif not b:
            open_from = min(open_from, int(a))
        else:
            result.update(range(int(a) if a else 1, int(b) + 1))
    return result, open_from


class _Errors:
    def __init__(self):
        self.hit = False

    def __call__(self):
        self.hit = True


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
                    raise ShellError(f"option requires an argument -- '{kind}'", 2)
                value = args[i]
                i += 1
            if kind == "e":
                patterns.append(value)
            else:
                patterns.extend(split_lines(ctx.fs.read_text(ctx.resolve(value))) if value != "-" else split_lines(ctx.stdin))
    a = Args.parse(rest, "invcrRlLEFwxohHqsIaZPU", "ABCm",
                   "binary-files=I,text=a,ignore-case=i,line-number=n,invert-match=v,count=c,recursive=r,files-with-matches=l,"
                   "files-without-match=L,extended-regexp=E,fixed-strings=F,word-regexp=w,line-regexp=x,only-matching=o,"
                   "quiet=q,silent=q,max-count=m,perl-regexp=P,no-filename=h,with-filename=H,no-messages=s,"
                   "color=U,colour=U,line-buffered=U,after-context=A,before-context=B,context=C,dereference-recursive=R")
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
                ctx.error(f"grep: warning: {lead.group(0)[-1]} at start of expression")
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
                ctx.error(f"grep: {f}: Is a directory")
            errors()
            continue

        above = {ctx.fs.real_path(path)[0] or path}  # the directories being walked

        def walk(d, display):
            try:
                names = ctx.fs.listdir(d)
            except ShellError:
                if not a.has("s"):
                    ctx.error(f"grep: {display}: Permission denied")
                errors()
                return
            for name in names:
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
                ctx.print(f"grep: {name}: binary file matches")
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
    a = Args.parse(Args.numeric_shorthand(ctx.args), "qvfF" if ctx.name == "tail" else "qv", "nc",
                   "lines=n,bytes=c,quiet=q,silent=q,verbose=v" + (",follow=f,retry=F" if ctx.name == "tail" else ""))
    if a.value("n") is not None:  # a bad count is reported before any file is opened
        _count_arg(a.value("n"), "lines")
    if a.value("c") is not None:
        _count_arg(a.value("c"), "bytes")
    errors = _Errors()
    items = list(inputs(ctx, a.positional, errors, raw=a.value("c") is not None))
    for k, (name, text) in enumerate(items):
        if (len(items) > 1 and not a.has("q")) or a.has("v"):
            ctx.print(("\n" if k else "") + f"==> {name} <==")
        if a.value("c") is not None:  # byte counts; text is bytes here
            spec = a.value("c")
            n = _count_arg(spec, "bytes")
            if head:
                chunk = text[: max(0, len(text) - n)] if spec.startswith("-") else text[:n]
            else:
                chunk = text[max(0, n - 1):] if spec.startswith("+") else text[max(0, len(text) - n):] if n else b""
            ctx.stdout.write(bytetext.decode(chunk))
            continue
        spec = a.value("n") or "10"
        n = _count_arg(spec, "lines")
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


def _count_arg(spec: str, what: str) -> int:
    try:
        return int(spec.lstrip("+-") or "x")
    except ValueError:
        raise ShellError(f"invalid number of {what}: {quote_l(spec)}") from None


def _wc(ctx):
    a = Args.parse(ctx.args, "lwcm", longs="lines=l,words=w,bytes=c,chars=m")
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

    a = Args.parse(ctx.args, "rnufhVgsbcCdiMRzZ", "kto",
                   "reverse=r,numeric-sort=n,unique=u,ignore-case=f,key=k,field-separator=t,output=o,general-numeric-sort=g,"
                   "human-numeric-sort=h,version-sort=V,stable=s,ignore-leading-blanks=b,check=c,dictionary-order=d,"
                   "month-sort=M,random-sort=R,ignore-nonprinting=i,zero-terminated=z,debug=Z")
    keys_spec = [ctx.args[i + 1] for i in range(len(ctx.args) - 1) if ctx.args[i] in ("-k", "--key")]
    keys_spec += [x[2:] for x in ctx.args if x.startswith("-k") and len(x) > 2]
    keys_spec += [x[6:] for x in ctx.args if x.startswith("--key=")]
    sep = a.value("t")
    if sep is not None and len(unescape(sep)) != 1:
        raise ShellError("multi-character tab '" + sep + "'", 2)
    sep = unescape(sep) if sep is not None else None
    global_opts = "".join(c for c in "bdfghnrMV" if a.has(c))
    keys = []  # (start field, start char, end field, end char, options)
    for spec in keys_spec:
        km = re.fullmatch(r"(\d+)(?:\.(\d+))?([a-zA-Z]*)(?:,(\d+)(?:\.(\d+))?([a-zA-Z]*))?", spec)
        if not km or int(km.group(1)) == 0:
            raise ShellError(f"invalid number at field start: invalid count at start of {quote_l(spec)}", 2)
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
                    ctx.error(f"sort: {named[0][0] if a.positional else '-'}:{i + 1}: disorder: {items[i][0]}")
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
    a = Args.parse(ctx.args, "cdui", longs="count=c,repeated=d,unique=u,ignore-case=i")
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
    a = Args.parse(ctx.args, "snz", "dfcbO", "delimiter=d,fields=f,characters=c,bytes=b,only-delimited=s,complement=z,"
                                             "output-delimiter=O")
    delim = a.value("d") if a.value("d") is not None else "\t"
    if len(delim) != 1:
        raise ShellError("the delimiter must be a single character")
    chars = a.value("c") or a.value("b")
    if not chars and not a.value("f"):
        raise UsageException("you must specify a list of bytes, characters, or fields")
    wanted, open_from = _parse_list(chars or a.value("f"))
    complement = a.has("z")

    def pick(items: list) -> list:
        return [x for i, x in enumerate(items, 1) if (i >= open_from or i in wanted) != complement]

    out_delim = a.value("O")
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
    a = Args.parse(ctx.args, "dscC", longs="delete=d,squeeze-repeats=s,complement=c")
    if not a.positional:
        raise UsageException("missing operand")
    if len(a.positional) > 2:
        raise UsageException(f"extra operand {quote_l(a.positional[2])}")
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
    a = Args.parse(ctx.args, "ai", longs="append=a,ignore-interrupts=i")
    code = 0
    for f in a.positional:
        path = ctx.resolve(f)
        if path in ("/dev/stdout", "/dev/stderr"):
            (ctx.stderr if path == "/dev/stderr" else ctx.stdout).write(ctx.stdin)
            continue
        try:
            (ctx.fs.append_text if a.has("a") else ctx.fs.write_text)(path, ctx.stdin)
        except ShellError as e:
            ctx.error(f"tee: {f}: {str(e).rsplit(': ', 1)[-1]}")
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
                raise ShellError(f"unmatched {kind} quote; by default quotes are special to xargs unless you use the -0 option")
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
    a = Args.parse(opts, "0rtpx", "InLdPsEa", "null=0,no-run-if-empty=r,verbose=t,max-args=n,max-lines=L,delimiter=d,"
                                            "max-procs=P,replace=I,arg-file=a,interactive=p,exit=x,max-chars=s")
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
                raise ShellError(f"value {a.value('L')} for -L option should be >= 1")
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
            raise ShellError(f"value {a.value('n')} for -n option should be >= 1")
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
        rc = await ctx.invoke(argv)
        if rc == 127:
            return 127
        if rc == 255:
            ctx.error(f"xargs: {argv[0]}: exited with status 255; aborting")
            return 124
        if rc != 0:
            code = 123
    return code


# ---------- sed ----------

class _SedCmd:
    def __init__(self):
        self.a1 = self.a2 = None  # ("line", n) | ("last",) | ("re", rx) | ("step", first, step) | ("zero",) | ("plus", n) | ("mult", n)
        self.negate = False
        self.in_range = False
        self.range_end = 0
        self.op = ""
        self.rx: re.Pattern | None = None
        self.repl: list = []
        self.glob = False
        self.nth = 1
        self.print = False
        self.text = ""
        self.label = ""
        self.target = 0  # jump target for b/t/T and for skipping a { block
        self.code = 0
        self.table: dict[int, str] = {}


# our own summary of the options this sed takes (not GNU's help text); also printed after a usage error
SED_USAGE = """Usage: sed [OPTION]... {script-only-if-no-other-script} [input-file]...
Run a script of editing commands over each line of the input and print the result.

  -n, --quiet, --silent    print only what the script prints (p, s///p, ...)
  -e script, --expression=script
                           add the commands in script
  -f script-file, --file=script-file
                           add the commands in a file
  -i[SUFFIX], --in-place[=SUFFIX]
                           write the result back into each file; with SUFFIX,
                           keep the original under the name FILE+SUFFIX
  -E, -r, --regexp-extended
                           read regular expressions as POSIX extended ones
  -s, --separate           treat each file as its own input, not one stream
  -u, --unbuffered         accepted, no effect
      --debug, --posix, --sandbox, --follow-symlinks
                           accepted, no effect
      --help               show this help
      --version            show the version

Without -e or -f, the first operand is the script. The other operands are
input files; with none, sed reads standard input.
"""
SED_HELP = SED_USAGE


def _sed_parse(parts: list[tuple[str, str]], extended: bool) -> list[_SedCmd]:
    """Parses a sed script into a flat command list; { } blocks and b/t/T labels become jump targets.
    parts: each -e expression and -f file, with where GNU says an error is ("-e expression #2", "file x.sed")."""
    script = "\n".join(t for t, _ in parts)
    starts = [0]
    for t, _ in parts[:-1]:
        starts.append(starts[-1] + len(t) + 1)
    cmds: list[_SedCmd] = []
    labels: dict[str, int] = {}
    blocks: list[tuple[int, int]] = []
    last_rx: list[re.Pattern | None] = [None]
    i, n = 0, len(script)

    def fail(message: str, pos: int, char_zero: bool = False) -> ShellError:
        """GNU's place for an error: how many characters of the expression were read (or the line of the file) at pos."""
        k = max([0] + [j for j in range(1, len(starts)) if starts[j] < pos])
        pos = min(pos, starts[k] + len(parts[k][0]))
        where = parts[k][1]
        at = f" line {1 + script.count(chr(10), starts[k], pos)}" if where.startswith("file ") else f", char {0 if char_zero else pos - starts[k]}"
        return ShellError(f"{where}{at}: {message}")

    def skip_blanks():
        nonlocal i
        while i < n and script[i] in " \t":
            i += 1

    # what: "regex" (of s), "addr" (an address regex), "replacement" or "y"; a regex's [brackets] may hold the delimiter
    def read_delimited(delim: str, what: str) -> str:
        nonlocal i
        regex = what in ("regex", "addr")
        in_bracket = False
        out = []
        while i < n and (script[i] != delim or in_bracket):
            ch = script[i]
            if ch == "\\" and i + 1 < n:
                if script[i + 1] == delim:
                    out.append(delim)
                elif script[i + 1] == "n" and regex:
                    out.append("\\n")
                elif in_bracket:  # a backslash is literal in brackets
                    out.append(ch)
                    i += 1
                    continue
                else:
                    out.append(script[i:i + 2])
                i += 2
                continue
            if ch == "\n":  # a newline in the replacement needs a backslash before it
                break
            if regex and not in_bracket and ch == "[":
                out.append(ch)
                i += 1
                if i < n and script[i] == "^":
                    out.append(script[i])
                    i += 1
                if i < n and script[i] == "]":
                    out.append(script[i])
                    i += 1
                in_bracket = True
                continue
            if in_bracket and ch == "[" and i + 1 < n and script[i + 1] in ":.=":
                close = script.find(script[i + 1] + "]", i + 2)
                if close >= 0 and script.find("\n", i, close) < 0:
                    out.append(script[i:close + 2])
                    i = close + 2
                    continue
            if ch == "]":
                in_bracket = False
            out.append(ch)
            i += 1
        if i >= n or script[i] != delim:
            raise fail({"addr": "unterminated address regex", "y": "unterminated `y' command"}.get(what, "unterminated `s' command"), i)
        i += 1
        return "".join(out)

    def compile_rx(text: str, flags: int, error_at: int) -> re.Pattern:
        if text == "":
            if last_rx[0] is None:
                raise fail("no previous regular expression", error_at)
            return last_rx[0]
        if (error := posix_regex_error(text, extended, grep=False)) is not None:
            raise ShellError(error, 4) if error == CLASS_SYNTAX else fail(error, error_at)
        try:
            rx = re.compile(posix_to_python(text, extended), flags)
        except re.error as e:
            raise fail(f"invalid regular expression: {e}", error_at) from None
        last_rx[0] = rx
        return rx

    def read_number() -> int:
        nonlocal i
        start = i
        while i < n and script[i].isdigit():
            i += 1
        return int(script[start:i])

    def read_addr(second: bool):
        nonlocal i
        if i >= n:
            return None
        ch = script[i]
        if second and ch in "+~" and i + 1 < n and script[i + 1].isdigit():
            i += 1
            return ("plus" if ch == "+" else "mult", read_number())
        if ch.isdigit():
            num = read_number()
            if not second and i < n and script[i] == "~":
                i += 1
                return ("step", num, read_number() if i < n and script[i].isdigit() else 0)
            return ("zero",) if num == 0 and not second else ("line", num)
        if ch == "$":
            i += 1
            return ("last",)
        if ch == "/" or (ch == "\\" and i + 1 < n):
            delim = "/" if ch == "/" else script[i + 1]
            i += 1 if ch == "/" else 2
            text = read_delimited(delim, "addr")
            flags = 0
            while i < n and script[i] in "IM":
                flags |= re.IGNORECASE if script[i] == "I" else re.MULTILINE
                i += 1
            skip_blanks()
            return ("re", compile_rx(text, flags, i))
        return None

    def read_label() -> str:
        nonlocal i
        skip_blanks()
        start = i
        while i < n and script[i] not in ";\n}":
            i += 1
        return script[start:i].strip()

    def read_text() -> str:
        r"""Text for a/i/c: `a text` or `a\` NEWLINE text, where a trailing backslash continues the text."""
        nonlocal i
        skip_blanks()
        if i >= n:
            raise fail("expected \\ after `a', `c' or `i'", i)
        if script[i] == "\\":
            i += 1
            if i < n and script[i] == "\n":
                i += 1
            else:
                skip_blanks()
        out = []
        while i < n and script[i] != "\n":
            if script[i] == "\\" and i + 1 < n:
                out.append(script[i + 1])
                i += 2
                continue
            out.append(script[i])
            i += 1
        return "".join(out)

    while True:
        while i < n and (script[i].isspace() or script[i] == ";"):
            i += 1
        if i >= n:
            break
        if script[i] == "#":
            while i < n and script[i] != "\n":
                i += 1
            continue
        c = _SedCmd()
        c.a1 = read_addr(False)
        if c.a1 is not None and i < n and script[i] == ",":
            i += 1
            skip_blanks()
            c.a2 = read_addr(True)
            if c.a2 is None:
                raise fail("unexpected `,'", min(i + 1, n))
        if c.a1 == ("zero",) and (c.a2 is None or c.a2[0] != "re"):
            raise fail("invalid usage of line address 0", min(i + 1, n))
        skip_blanks()
        if i < n and script[i] == "!":
            c.negate = True
            i += 1
            skip_blanks()
            if i < n and script[i] == "!":
                raise fail("multiple `!'s", i + 1)
        if i >= n:
            raise fail("missing command", i)
        c.op = script[i]
        i += 1
        op = c.op
        at = i
        if op == "{":
            blocks.append((len(cmds), i))
        elif op == "}":
            if not blocks:
                raise fail("unexpected `}'", i)
            if c.a1 is not None:
                raise fail("} doesn't want any addresses", i)
            cmds[blocks.pop()[0]].target = len(cmds) + 1
        elif op == "s":
            if i >= n:
                raise fail("unterminated `s' command", i)
            delim = script[i]
            i += 1
            find = read_delimited(delim, "regex")
            c.repl = _sed_replacement(read_delimited(delim, "replacement"))
            flags = 0
            numbered = False
            while i < n:
                f = script[i]
                if f in "0123456789":
                    c.nth = read_number()
                    if numbered:
                        raise fail("multiple number options to `s' command", i)
                    if c.nth == 0:
                        raise fail("number option to `s' command may not be zero", i)
                    numbered = True
                    continue
                if f not in "gpiImMew":
                    break
                i += 1
                if f == "g" and c.glob:
                    raise fail("multiple `g' options to `s' command", i)
                if f == "p" and c.print:
                    raise fail("multiple `p' options to `s' command", i)
                if f == "g":
                    c.glob = True
                elif f == "p":
                    c.print = True
                elif f in "iI":
                    flags |= re.IGNORECASE
                elif f in "mM":
                    flags |= re.MULTILINE
                elif f == "w" and not script[i:].split("\n", 1)[0].strip():
                    raise fail("missing filename in r/R/w/W commands", i)
                else:
                    raise fail(f"the `{f}' flag of `s' is not supported", i)
            skip_blanks()
            if i < n and script[i] not in ";\n}#":
                raise fail("unknown option to `s'", i + 1)
            # GNU compiles the regex once it has read the character that ends the command
            compiled_at = i + 1 if i < n and script[i] in ";\n" else i
            c.rx = compile_rx(find, flags, compiled_at)
            refs = [p for p in c.repl if isinstance(p, int)]
            if refs and max(refs) > c.rx.groups:
                raise fail(f"invalid reference \\{max(refs)} on `s' command's RHS", compiled_at)
        elif op == "y":
            delim = script[i] if i < n else ""
            i += 1
            src = unescape(read_delimited(delim, "y"))
            dst = unescape(read_delimited(delim, "y"))
            if len(src) != len(dst):
                raise fail("strings for `y' command are different lengths", i)
            c.table = {ord(a): b for a, b in zip(src, dst)}
        elif op in "aic":
            c.text = read_text()
        elif op == ":":
            if c.a1 is not None:
                raise fail(": doesn't want any addresses", i)
            label = read_label()
            if not label:
                raise fail("\":\" lacks a label", i)
            labels[label] = len(cmds)
        elif op in "btT":
            c.label = read_label()
        elif op == "v":
            read_label()  # "requires GNU sed VERSION": always met
        elif op in "qQ":
            skip_blanks()
            c.code = read_number() if i < n and script[i].isdigit() else 0
        else:
            if op in "rRwW" and not read_label():
                raise fail("missing filename in r/R/w/W commands", i)
            if op not in "=dDgGhHnNpPxz":
                raise fail(f"unknown command: `{op}'", at)
        cmds.append(c)
        skip_blanks()
        if op != "{" and i < n and script[i] not in ";\n}#":
            raise fail("extra characters after command", i + 1)
    if blocks:
        raise fail("unmatched `{'", blocks[-1][1], char_zero=True)
    for c in cmds:
        if c.op in "btT":
            if c.label and c.label not in labels:
                raise ShellError(f"can't find label for jump to `{c.label}'", 4)
            c.target = labels[c.label] if c.label else len(cmds)
    return cmds


def _sed_replacement(r: str) -> list:
    """Compiles a sed replacement into parts: str literals, int groups (& is 0), and ("case", U/L/u/l/E) markers."""
    parts: list = []
    i = 0
    while i < len(r):
        c = r[i]
        if c == "\\" and i + 1 < len(r):
            nx = r[i + 1]
            if nx.isdigit():
                parts.append(int(nx))
            elif nx in "ULulE":
                parts.append(("case", nx))
            else:
                parts.append({"n": "\n", "t": "\t", "r": "\r"}.get(nx, nx))
            i += 2
            continue
        parts.append(0 if c == "&" else c)
        i += 1
    return parts


def _sed_expand(parts: list, m: re.Match, size: int = 0, max_string: int = 1 << 62) -> str:
    """The replacement for m; size is what the result already holds, so it stops at the string limit before growing."""
    out = []
    mode = one = None
    for p in parts:
        if isinstance(p, tuple):
            if p[1] in "ULE":
                mode, one = (None if p[1] == "E" else p[1]), None
            else:
                one = p[1]
            continue
        text = (m.group(p) or "") if isinstance(p, int) else p
        if mode:
            text = text.upper() if mode == "U" else text.lower()
        if one and text:
            text = (text[0].upper() if one == "u" else text[0].lower()) + text[1:]
            one = None
        size += len(text)
        if size > max_string:
            raise _too_large(max_string)
        out.append(text)
    return "".join(out)


def _too_large(max_string: int):
    from ..shell import LimitExceeded, size_text
    return LimitExceeded(f"sed: pattern space too large (limit {size_text(max_string)}); process the input in smaller pieces")


def _sed_substitute(c: _SedCmd, ps: str, max_string: int = 1 << 62) -> tuple[str, bool]:
    out: list[str] = []
    size = 0
    pos, count, prev_end, done = 0, 0, -1, False
    for m in c.rx.finditer(ps):
        if m.start() == m.end() == prev_end:
            continue  # no empty match right after the previous match (GNU: "abc" s/b*/X/g -> XaXcX)
        prev_end = m.end()
        count += 1
        if count < c.nth:
            continue
        out.append(ps[pos:m.start()])
        size += m.start() - pos
        piece = _sed_expand(c.repl, m, size, max_string)
        size += len(piece)
        out.append(piece)
        pos, done = m.end(), True
        if not c.glob:
            break
    if not done:
        return ps, False
    out.append(ps[pos:])
    return "".join(out), True


def _sed_matches(c: _SedCmd, ps: str, n: int, last: bool) -> bool:
    def hit(addr) -> bool:
        kind = addr[0]
        if kind == "line":
            return addr[1] == n
        if kind == "last":
            return last
        if kind == "re":
            return bool(addr[1].search(ps))
        if kind == "step":
            return n == addr[1] if addr[2] <= 0 else n >= addr[1] and (n - addr[1]) % addr[2] == 0
        return n == 1  # zero: 0,/re/ starts before the first line

    if c.a1 is None:
        result = True
    elif c.a2 is None:
        result = hit(c.a1)
    elif c.in_range:
        kind = c.a2[0]
        if kind == "line":
            ended = n >= c.a2[1]
        elif kind == "plus":
            ended = n >= c.range_end
        elif kind == "mult":
            ended = c.a2[1] <= 0 or n % c.a2[1] == 0
        else:
            ended = hit(c.a2)
        c.in_range = not ended
        result = True
    elif hit(c.a1):
        kind = c.a2[0]
        if c.a1[0] == "zero":
            c.in_range = not hit(c.a2)
        elif kind == "line":
            c.in_range = c.a2[1] > n
        elif kind == "plus":
            c.range_end = n + c.a2[1]
            c.in_range = c.a2[1] > 0
        elif kind == "mult":
            c.in_range = c.a2[1] > 0 and n % c.a2[1] != 0
        elif kind == "last":
            c.in_range = not last
        else:
            c.in_range = True
        result = True
    else:
        result = False
    return result != c.negate


def _sed_execute(cmds: list[_SedCmd], text: str, out, quiet: bool, ctx=None) -> int | None:
    """Runs a parsed script over text. Returns the q/Q exit code if the script quit, else None."""
    lines = split_lines(text)
    trailing = text.endswith("\n")
    for c in cmds:
        c.in_range = False
    hold = ""
    idx = n = 0
    pending_newline = False

    def write(s: str, from_input: bool = False):
        nonlocal pending_newline
        if pending_newline:
            out.write("\n")
            pending_newline = False
        out.write(s)
        if from_input and idx >= len(lines) and not trailing:
            pending_newline = True  # the last input line had no newline: don't invent one (unless more output follows)
        else:
            out.write("\n")

    exit_code = None
    ps = ""
    reread = True
    steps = 0
    max_string = ctx.shell.max_string_bytes if ctx is not None else 1 << 62
    while exit_code is None:
        if reread:
            if idx >= len(lines):
                break
            ps = lines[idx]
            idx += 1
            n += 1
        reread = True
        appends: list[str] = []
        tflag = False
        autoprint = not quiet
        pc = 0
        while pc < len(cmds):
            steps += 1
            if steps > 10_000_000:
                raise ShellError("too many steps (a branch loops forever?)")
            if ctx is not None and steps & 1023 == 0:
                ctx.check_time()  # the call's time limit, or `timeout`
            if len(ps) > max_string or len(hold) > max_string:
                raise _too_large(max_string)
            c = cmds[pc]
            pc += 1
            if not _sed_matches(c, ps, n, idx >= len(lines)):
                if c.op == "{":
                    pc = c.target
                continue
            op = c.op
            if op == "s":
                ps, changed = _sed_substitute(c, ps, max_string)
                if changed:
                    tflag = True
                    if c.print:
                        write(ps, True)
            elif op == "p":
                write(ps, True)
            elif op == "P":
                write(ps.split("\n", 1)[0], True)
            elif op == "d":
                autoprint = False
                break
            elif op == "D":
                if "\n" not in ps:
                    autoprint = False
                    break
                ps = ps.split("\n", 1)[1]
                autoprint, reread = False, False
                break
            elif op == "n":
                if idx >= len(lines):
                    break  # no next line: GNU sed ends here, printing the pattern space
                if not quiet:
                    write(ps, True)
                for t in appends:
                    write(t)
                appends = []
                ps = lines[idx]
                idx += 1
                n += 1
            elif op == "N":
                if idx >= len(lines):
                    break
                ps += "\n" + lines[idx]
                idx += 1
                n += 1
            elif op == "g":
                ps = hold
            elif op == "G":
                ps += "\n" + hold
            elif op == "h":
                hold = ps
            elif op == "H":
                hold += "\n" + ps
            elif op == "x":
                ps, hold = hold, ps
            elif op == "z":
                ps = ""
            elif op == "y":
                ps = ps.translate(c.table)
            elif op == "=":
                write(str(n))
            elif op == "a":
                appends.append(c.text)
            elif op == "i":
                write(c.text)
            elif op == "c":
                if c.a2 is None or not c.in_range or c.negate:
                    write(c.text)
                autoprint = False
                break
            elif op == "b":
                pc = c.target
            elif op == "t":
                if tflag:
                    tflag = False
                    pc = c.target
            elif op == "T":
                if not tflag:
                    pc = c.target
                else:
                    tflag = False
            elif op == "q":
                exit_code = c.code
                break
            elif op == "Q":
                exit_code = c.code
                autoprint = False
                break
        if autoprint:
            write(ps, True)
        for t in appends:
            write(t)
    return exit_code


def _sed(ctx):
    def bad(message: str) -> int:  # a bad command line: the complaint, then the whole usage text, on stderr
        ctx.stderr.write((f"sed: {message}\n" if message else "") + SED_USAGE)
        return 1

    scripts: list[tuple[str, str]] = []
    expressions = 0

    def add_script(kind: str, value: str) -> None:
        nonlocal expressions
        if kind == "e":
            expressions += 1
            scripts.append((value, f"-e expression #{expressions}"))
            return
        try:
            text = ctx.stdin if value == "-" else ctx.fs.read_text(ctx.resolve(value))
        except ShellError as e:
            raise ShellError(f"couldn't open file {value}: {str(e).rsplit(': ', 1)[-1]}", 4) from None
        scripts.append((text, f"file {value}"))

    files: list[str] = []
    quiet = extended = separate = in_place = False
    suffix = ""
    args = list(ctx.args)
    i = 0
    while i < len(args):
        arg = args[i]
        i += 1
        if arg == "--":
            files.extend(args[i:])
            break
        if arg.startswith("--"):
            name, eq, value = arg[2:].partition("=")
            if name in ("quiet", "silent"):
                quiet = True
            elif name == "regexp-extended":
                extended = True
            elif name == "separate":
                separate = True
            elif name == "in-place":
                in_place, suffix = True, value
            elif name in ("expression", "file"):
                if not eq:
                    if i >= len(args):
                        return bad(f"option '--{name}' requires an argument")
                    value = args[i]
                    i += 1
                add_script(name[0], value)
            elif name in ("posix", "debug", "sandbox", "follow-symlinks", "unbuffered"):
                pass
            elif name == "help":
                ctx.stdout.write(SED_HELP)
                return 0
            else:
                return bad(f"unrecognized option '{arg}'")
            continue
        if len(arg) < 2 or arg[0] != "-":
            files.append(arg)
            continue
        k = 1
        while k < len(arg):
            f = arg[k]
            k += 1
            if f == "n":
                quiet = True
            elif f in "Er":
                extended = True
            elif f == "s":
                separate = True
            elif f == "u":
                pass
            elif f == "i":
                in_place, suffix = True, arg[k:]  # like GNU, -i takes the rest of the word as a backup suffix
                break
            elif f in "ef":
                value = arg[k:]
                if not value:
                    if i >= len(args):
                        return bad(f"option requires an argument -- '{f}'")
                    value = args[i]
                    i += 1
                add_script(f, value)
                break
            else:
                return bad(f"invalid option -- '{f}'")
    if not scripts:
        if not files:
            return bad("")
        add_script("e", files.pop(0))
    cmds = _sed_parse(scripts, extended)
    if in_place:
        if not files:
            raise ShellError("no input files", 1)
        code = 0
        for f in files:
            path = ctx.resolve(f)
            try:
                original = ctx.fs.read_text(path)
            except ShellError as e:
                if ctx.fs.is_dir(path):
                    ctx.error(f"sed: couldn't edit {f}: not a regular file")
                    code = 4
                else:
                    ctx.error(f"sed: can't read {f}: {str(e).rsplit(': ', 1)[-1]}")
                    code = 2
                continue
            buf = io.StringIO()
            quit_code = _sed_execute(cmds, original, buf, quiet, ctx)
            if suffix:
                backup = suffix.replace("*", f.rsplit("/", 1)[-1]) if "*" in suffix else f + suffix
                ctx.fs.write_text(ctx.resolve(backup), original)
            ctx.fs.write_text(path, buf.getvalue())
            if quit_code is not None:
                return quit_code
        return code
    errors = _Errors()
    texts = [t for _, t in inputs(ctx, files, errors)]
    if separate:
        for t in texts:
            quit_code = _sed_execute(cmds, t, ctx.stdout, quiet, ctx)
            if quit_code is not None:
                return quit_code
    else:
        joined = "".join(t if t.endswith("\n") or k == len(texts) - 1 else t + "\n" for k, t in enumerate(texts))
        quit_code = _sed_execute(cmds, joined, ctx.stdout, quiet, ctx)
        if quit_code is not None:
            return quit_code
    return 4 if any(f != "-" and ctx.fs.is_dir(ctx.resolve(f)) for f in files) else 2 if errors.hit else 0  # 4: a read error (a directory)


def register(shell) -> None:
    r = shell.register_command
    r("grep", _grep, "Search text with regular expressions")
    # Ubuntu's egrep and fgrep are scripts that run `grep -E` / `grep -F`, so messages say "grep:"
    for alias, flag in (("egrep", "-E"), ("fgrep", "-F")):
        r(alias, lambda ctx, flag=flag: _grep(CommandContext(ctx.shell, "grep", [flag, *ctx.args], ctx._stdin, ctx.stdout, ctx.stderr)),
          f"Search text, like grep {flag}")
    r("head", _head_tail, "Print the first lines of files")
    r("tail", _head_tail, "Print the last lines of files")
    r("wc", _wc, "Count lines, words and bytes")
    r("sort", _sort, "Sort lines")
    r("uniq", _uniq, "Collapse adjacent duplicate lines")
    r("cut", _cut, "Select fields or characters from lines")
    r("tr", _tr, "Translate or delete characters")
    r("tee", _tee, "Copy stdin to stdout and files")
    r("xargs", _xargs, "Build and run commands from stdin")
    r("sed", _sed, "Stream editor", SED_HELP)
