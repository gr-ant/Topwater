"""Text tools: grep head tail wc sort uniq cut tr tee xargs sed."""
from __future__ import annotations

import re

from ..shell import glob_to_regex
from ..vfs import ShellError, normalize
from . import Args, inputs, split_lines, unescape, write_lines


def bre_to_python(bre: str) -> str:
    """POSIX basic regex (grep/sed default) -> Python: \\( \\) \\{ \\} \\| \\+ \\? are operators, bare ones are literals."""
    out = []
    i = 0
    while i < len(bre):
        c = bre[i]
        if c == "\\" and i + 1 < len(bre):
            n = bre[i + 1]
            out.append(n if n in "(){}|+?" else "\\" + n)
            i += 2
            continue
        out.append("\\" + c if c in "(){}|+?" else c)
        i += 1
    return "".join(out)


def _regex(pattern: str, extended: bool, fixed: bool, ignore_case: bool, word: bool = False) -> re.Pattern:
    rx = re.escape(pattern) if fixed else pattern if extended else bre_to_python(pattern)
    if word:
        rx = rf"\b(?:{rx})\b"
    try:
        return re.compile(rx, re.IGNORECASE if ignore_case else 0)
    except re.error as e:
        raise ShellError(f"invalid regular expression: {e}", 2) from None


def _parse_list(spec: str) -> list[int]:
    """"1,3-5,7-" -> sorted 1-based indexes (open ranges capped at 1000)."""
    result = set()
    for part in spec.split(","):
        if "-" in part:
            a, b = part.split("-", 1)
            result.update(range(int(a) if a else 1, (int(b) if b else 1000) + 1))
        else:
            result.add(int(part))
    return sorted(result)


class _Errors:
    def __init__(self):
        self.hit = False

    def __call__(self):
        self.hit = True


def _grep(ctx):
    # --include/--exclude/--exclude-dir may repeat, so pull them out before getopt-style parsing
    include, exclude, exclude_dir, rest = [], [], [], []
    for arg in ctx.args:
        for prefix, target in (("--include=", include), ("--exclude=", exclude), ("--exclude-dir=", exclude_dir)):
            if arg.startswith(prefix):
                target.append(glob_to_regex(arg[len(prefix):]))
                break
        else:
            rest.append(arg)
    a = Args.parse(rest, "invcrRlLEFwohHqsIaZ", "eABCm",
                   "binary-files=I,text=a,ignore-case=i,line-number=n,invert-match=v,count=c,recursive=r,files-with-matches=l,"
                   "files-without-match=L,extended-regexp=E,fixed-strings=F,word-regexp=w,only-matching=o,"
                   "quiet=q,regexp=e,max-count=m")
    positional = list(a.positional)
    if a.value("e") is not None:
        pattern = a.value("e")
    elif positional:
        pattern = positional.pop(0)
    else:
        raise ShellError("usage: grep [OPTION]... PATTERNS [FILE]...", 2)
    recursive = a.has("r") or a.has("R")
    if recursive and not positional:
        positional = ["."]
    rx = _regex(pattern, a.has("E"), a.has("F"), a.has("i"), a.has("w"))
    after = int(a.value("A") or a.value("C") or 0)
    before = int(a.value("B") or a.value("C") or 0)
    max_count = int(a.value("m")) if a.value("m") else 10**9

    files: list[str] = []
    errors = _Errors()
    for f in positional:
        path = ctx.resolve(f)
        if not ctx.fs.is_dir(path):
            files.append(f)
            continue
        if not recursive:
            if not a.has("s"):
                ctx.error(f"grep: {f}: Is a directory")
            errors()
            continue

        def walk(d, display):
            for name in ctx.fs.listdir(d):
                child = normalize(d, name)
                child_display = name if display == "." else display.rstrip("/") + "/" + name
                if ctx.fs.is_dir(child):
                    if not any(r.match(name) for r in exclude_dir):
                        walk(child, child_display)
                elif (not include or any(r.match(name) for r in include)) and not any(r.match(name) for r in exclude):
                    files.append(child_display)

        walk(path, f)
    if positional and not files:
        return 2 if errors.hit else 1

    code = 1
    show_name = not a.has("h") and (a.has("H") or len(files) > 1 or recursive)
    for name, text in inputs(ctx, files, errors):
        if text.startswith("\x7fELF") and not a.has("a"):
            if not a.has("I") and rx.search(text):
                ctx.print(f"grep: {name}: binary file matches")
                code = 0
            continue
        lines = split_lines(text)
        hits = []
        for i, line in enumerate(lines):
            if len(hits) >= max_count:
                break
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
                            ctx.print(head + m.group(0))
                else:
                    ctx.print(head + lines[i])
                last_printed = i
    return 2 if errors.hit and code != 0 else code


def _head_tail(ctx):
    head = ctx.name == "head"
    a = Args.parse(Args.numeric_shorthand(ctx.args), "qv", "nc", "lines=n,bytes=c,quiet=q")
    errors = _Errors()
    items = list(inputs(ctx, a.positional, errors))
    for k, (name, text) in enumerate(items):
        if len(items) > 1 and not a.has("q"):
            ctx.print(("\n" if k else "") + f"==> {name} <==")
        if a.value("c") is not None:
            n = int(a.value("c"))
            ctx.stdout.write(text[:n] if head else text[max(0, len(text) - n):])
            continue
        spec = a.value("n") or "10"
        lines = split_lines(text)
        if head:
            pick = lines[: max(0, len(lines) - int(spec[1:]))] if spec.startswith("-") else lines[: int(spec)]
        elif spec.startswith("+"):
            pick = lines[int(spec[1:]) - 1:]
        else:
            n = int(spec.lstrip("-"))
            pick = lines[-n:] if n else []
        write_lines(ctx.stdout, pick)
    return 1 if errors.hit else 0


def _wc(ctx):
    a = Args.parse(ctx.args, "lwcm", longs="lines=l,words=w,bytes=c,chars=m")
    every = not (a.flags & set("lwcm"))
    errors = _Errors()
    items = list(inputs(ctx, a.positional, errors))
    totals = [0, 0, 0]

    def row(l, w, c, name):
        parts = []
        if every or a.has("l"):
            parts.append(str(l))
        if every or a.has("w"):
            parts.append(str(w))
        if every or a.has("c") or a.has("m"):
            parts.append(str(c))
        if name is not None:
            parts.append(name)
        return " ".join(parts)

    for name, text in items:
        l, w = text.count("\n"), len(text.split())
        c = len(text) if a.has("m") else len(text.encode())
        totals = [totals[0] + l, totals[1] + w, totals[2] + c]
        ctx.print(row(l, w, c, name if a.positional else None))
    if len(items) > 1:
        ctx.print(row(*totals, "total"))
    return 1 if errors.hit else 0


def _sort(ctx):
    a = Args.parse(ctx.args, "rnufhVgsbZ", "kto",
                   "reverse=r,numeric-sort=n,unique=u,ignore-case=f,key=k,field-separator=t,output=o,general-numeric-sort=g,"
                   "human-numeric-sort=h,version-sort=V,stable=s,ignore-leading-blanks=b,debug=Z")
    errors = _Errors()
    lines = [line for _, text in inputs(ctx, a.positional, errors) for line in split_lines(text)]
    sep = a.value("t")
    # -k START[,END] with per-key modifiers, e.g. -k2,2nr
    k_start, k_end, key_opts = 0, 10**9, ""
    if a.value("k"):
        km = re.fullmatch(r"(\d+)(?:\.\d+)?([a-zA-Z]*)(?:,(\d+)(?:\.\d+)?([a-zA-Z]*))?", a.value("k"))
        if not km:
            raise ShellError(f"invalid key specification '{a.value('k')}'", 2)
        k_start = int(km.group(1))
        if km.group(3):
            k_end = int(km.group(3))
        key_opts = km.group(2) + (km.group(4) or "")

    def opt(c):
        return c in key_opts if key_opts else a.has(c)

    def field(line):
        if not k_start:
            return line
        fields = line.split(sep[0]) if sep else line.split()
        if k_start > len(fields):
            return ""
        return (sep or " ").join(fields[k_start - 1:min(k_end, len(fields))])

    def num(s):
        m = re.match(r"-?\d+(\.\d+)?", s.lstrip())
        return float(m.group(0)) if m else 0.0

    if opt("n") or opt("h") or opt("g") or opt("V"):
        lines.sort(key=lambda l: (num(field(l)), l))
    else:
        lines.sort(key=lambda l: ((field(l).lower() if opt("f") else field(l)), l))
    if opt("r") or (key_opts and a.has("r")):
        lines.reverse()
    if a.has("u"):
        seen, unique = set(), []
        for l in lines:
            k = l.lower() if a.has("f") else l
            if k not in seen:
                seen.add(k)
                unique.append(l)
        lines = unique
    output = "".join(l + "\n" for l in lines)
    if a.value("o"):
        ctx.fs.write_text(ctx.resolve(a.value("o")), output)
    else:
        ctx.stdout.write(output)
    return 1 if errors.hit else 0


def _uniq(ctx):
    a = Args.parse(ctx.args, "cdui", longs="count=c,repeated=d,unique=u,ignore-case=i")
    text = ctx.fs.read_text(ctx.resolve(a.positional[0])) if a.positional else ctx.stdin
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
    a = Args.parse(ctx.args, "s", "dfc", "delimiter=d,fields=f,characters=c,only-delimited=s")
    delim = a.value("d") or "\t"
    if len(delim) != 1:
        raise ShellError("the delimiter must be a single character")
    errors = _Errors()
    for _, text in inputs(ctx, a.positional, errors):
        for line in split_lines(text):
            if a.value("c"):
                ctx.print("".join(line[i - 1] for i in _parse_list(a.value("c")) if i <= len(line)))
                continue
            if not a.value("f"):
                raise ShellError("you must specify a list of bytes, characters, or fields")
            if delim not in line:
                if not a.has("s"):
                    ctx.print(line)
                continue
            fields = line.split(delim)
            ctx.print(delim.join(fields[i - 1] for i in _parse_list(a.value("f")) if i <= len(fields)))
    return 1 if errors.hit else 0


def _expand_set(spec: str) -> str:
    for cls, chars in (("[:upper:]", "A-Z"), ("[:lower:]", "a-z"), ("[:digit:]", "0-9"), ("[:alpha:]", "a-zA-Z"),
                       ("[:alnum:]", "a-zA-Z0-9"), ("[:space:]", " \t\n\r\f\v")):
        spec = spec.replace(cls, chars)
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
    a = Args.parse(ctx.args, "dsc", longs="delete=d,squeeze-repeats=s")
    if not a.positional:
        raise ShellError("missing operand")
    set1 = _expand_set(a.positional[0])
    set2 = _expand_set(a.positional[1]) if len(a.positional) > 1 else ""
    out: list[str] = []
    squeeze = set2 or set1
    for ch in ctx.stdin:
        idx = set1.find(ch)
        if a.has("d"):
            if idx < 0:
                out.append(ch)
            continue
        o = set2[min(idx, len(set2) - 1)] if idx >= 0 and set2 else ch
        if a.has("s") and out and out[-1] == o and o in squeeze:
            continue
        out.append(o)
    ctx.stdout.write("".join(out))


def _tee(ctx):
    a = Args.parse(ctx.args, "a", longs="append=a")
    for f in a.positional:
        (ctx.fs.append_text if a.has("a") else ctx.fs.write_text)(ctx.resolve(f), ctx.stdin)
    ctx.stdout.write(ctx.stdin)


async def _xargs(ctx):
    opts = []
    i = 0
    args = ctx.args
    while i < len(args) and args[i].startswith("-"):
        opts.append(args[i])
        if args[i] in ("-I", "-n", "-d") and i + 1 < len(args):
            i += 1
            opts.append(args[i])
        i += 1
    a = Args.parse(opts, "0rt", "Ind")
    command = list(args[i:]) or ["echo"]
    data = ctx.stdin
    if a.has("r") and not data.strip():
        return 0
    code = 0
    if a.value("I") is not None:
        rep = a.value("I")
        for line in split_lines(data):
            if line and await ctx.invoke([c.replace(rep, line) for c in command]) != 0:
                code = 123
        return code
    if a.has("0"):
        items = [x for x in data.split("\0") if x]
    elif a.value("d") is not None:
        items = [x for x in data.split(unescape(a.value("d"))[0]) if x]
    else:
        items = data.split()
    batch = int(a.value("n")) if a.value("n") else max(1, len(items))
    for k in range(0, max(1, len(items)), batch):
        if await ctx.invoke(command + items[k:k + batch]) != 0:
            code = 123
    return code


# ---------- sed ----------

class _SedCmd:
    def __init__(self):
        self.a1 = self.a2 = None  # ("line", n) | ("last",) | ("re", pattern)
        self.negate = False
        self.in_range = False
        self.op = ""
        self.find: re.Pattern | None = None
        self.replace = None
        self.glob = False
        self.print = False
        self.text = ""


def _sed_parse(script: str, extended: bool) -> list[_SedCmd]:
    cmds = []
    i = 0

    def read_delimited(delim):
        nonlocal i
        out = []
        while i < len(script) and script[i] != delim:
            if script[i] == "\\" and i + 1 < len(script):
                if script[i + 1] == delim:
                    out.append(delim)
                    i += 2
                    continue
                out.append("\\")
                i += 1
            out.append(script[i])
            i += 1
        if i >= len(script):
            raise ShellError("unterminated `s' command")
        i += 1
        return "".join(out)

    def make_regex(rx, ignore_case):
        try:
            return re.compile(rx if extended else bre_to_python(rx), re.IGNORECASE if ignore_case else 0)
        except re.error as e:
            raise ShellError(f"invalid regular expression: {e}") from None

    def read_addr():
        nonlocal i
        if i >= len(script):
            return None
        if script[i].isdigit():
            s = i
            while i < len(script) and script[i].isdigit():
                i += 1
            return ("line", int(script[s:i]))
        if script[i] == "$":
            i += 1
            return ("last",)
        if script[i] == "/":
            i += 1
            return ("re", make_regex(read_delimited("/"), False))
        return None

    while i < len(script):
        while i < len(script) and (script[i].isspace() or script[i] == ";"):
            i += 1
        if i >= len(script):
            break
        c = _SedCmd()
        c.a1 = read_addr()
        if c.a1 and i < len(script) and script[i] == ",":
            i += 1
            c.a2 = read_addr()
            if c.a2 is None:
                raise ShellError("unexpected `,'")
        while i < len(script) and script[i] == " ":
            i += 1
        if i < len(script) and script[i] == "!":
            c.negate = True
            i += 1
        if i >= len(script):
            raise ShellError("missing command")
        c.op = script[i]
        i += 1
        if c.op == "s":
            delim = script[i]
            i += 1
            find = read_delimited(delim)
            repl = read_delimited(delim)
            ignore_case = False
            while i < len(script) and script[i] in "giIp":
                f = script[i]
                i += 1
                if f == "g":
                    c.glob = True
                elif f == "p":
                    c.print = True
                else:
                    ignore_case = True
            c.find = make_regex(find, ignore_case)
            c.replace = _sed_replacement(repl)
        elif c.op in "aic":
            end = script.find("\n", i)
            end = len(script) if end < 0 else end
            c.text = script[i:end].lstrip().lstrip("\\").lstrip()
            i = end
        elif c.op not in "pdq":
            raise ShellError(f"unknown command: `{c.op}' (supported: s p d q a i c)")
        cmds.append(c)
    return cmds


def _sed_replacement(r: str):
    """Compiles a sed replacement (&, \\1, \\n) into a function for re.sub."""
    parts: list = []  # str literal or int group
    i = 0
    while i < len(r):
        c = r[i]
        if c == "\\" and i + 1 < len(r):
            n = r[i + 1]
            parts.append(int(n) if n.isdigit() else {"n": "\n", "t": "\t"}.get(n, n))
            i += 2
            continue
        parts.append(0 if c == "&" else c)
        i += 1
    return lambda m: "".join(m.group(p) or "" if isinstance(p, int) else p for p in parts)


def _sed_matches(c: _SedCmd, line: str, n: int, last: bool) -> bool:
    def hit(addr):
        kind = addr[0]
        return addr[1] == n if kind == "line" else last if kind == "last" else bool(addr[1].search(line))

    if c.a1 is None:
        result = True
    elif c.a2 is None:
        result = hit(c.a1)
    elif c.in_range:
        if (n >= c.a2[1]) if c.a2[0] == "line" else hit(c.a2):
            c.in_range = False
        result = True
    elif hit(c.a1):
        c.in_range = not (c.a2[0] == "line" and c.a2[1] <= n)
        result = True
    else:
        result = False
    return result != c.negate


def _sed_execute(cmds, text: str, out, quiet: bool) -> None:
    lines = split_lines(text)
    trailing = text.endswith("\n")
    for c in cmds:
        c.in_range = False
    for n, line in enumerate(lines):
        deleted = quit_ = False
        last = n == len(lines) - 1
        for c in cmds:
            if not _sed_matches(c, line, n + 1, last):
                continue
            if c.op == "s":
                new, count = c.find.subn(c.replace, line, count=0 if c.glob else 1)
                if c.print and count:
                    out.write(new + "\n")
                line = new
            elif c.op == "p":
                out.write(line + "\n")
            elif c.op == "d":
                deleted = True
            elif c.op == "q":
                quit_ = True
            elif c.op == "a":
                line += "\n" + c.text
            elif c.op == "i":
                line = c.text + "\n" + line
            elif c.op == "c":
                line = c.text
            if deleted or quit_:
                break
        if not deleted and not quiet:
            out.write(line + ("" if last and not trailing else "\n"))
        if quit_:
            break


def _sed(ctx):
    import io

    a = Args.parse(ctx.args, "niEr", "e", "quiet=n,silent=n,in-place=i,regexp-extended=E,expression=e")
    positional = list(a.positional)
    scripts = [ctx.args[i + 1] for i in range(len(ctx.args) - 1) if ctx.args[i] in ("-e", "--expression")]
    if not scripts:
        if not positional:
            raise ShellError("usage: sed [-n] [-i] [-E] SCRIPT [FILE]...")
        scripts.append(positional.pop(0))
    extended = a.has("E") or a.has("r")
    cmds = [c for s in scripts for c in _sed_parse(s, extended)]
    if a.has("i"):
        if not positional:
            raise ShellError("no input files")
        for f in positional:
            path = ctx.resolve(f)
            buf = io.StringIO()
            _sed_execute(cmds, ctx.fs.read_text(path), buf, a.has("n"))
            ctx.fs.write_text(path, buf.getvalue())
        return 0
    errors = _Errors()
    text = "".join(t for _, t in inputs(ctx, positional, errors))
    _sed_execute(cmds, text, ctx.stdout, a.has("n"))
    return 2 if errors.hit else 0


def register(shell) -> None:
    r = shell.register_command
    r("grep", _grep, "Search text with regular expressions")
    r("head", _head_tail, "Print the first lines of files")
    r("tail", _head_tail, "Print the last lines of files")
    r("wc", _wc, "Count lines, words and bytes")
    r("sort", _sort, "Sort lines")
    r("uniq", _uniq, "Collapse adjacent duplicate lines")
    r("cut", _cut, "Select fields or characters from lines")
    r("tr", _tr, "Translate or delete characters")
    r("tee", _tee, "Copy stdin to stdout and files")
    r("xargs", _xargs, "Build and run commands from stdin")
    r("sed", _sed, "Stream editor (s///, p, d, q with line/regex addresses)")
