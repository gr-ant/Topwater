"""sed: the stream editor (the command is registered with the text commands). See docs/architecture.md."""
from __future__ import annotations

import io
import re

from .._spec import COMMANDS, GETOPT
from ..format import unescape
from ..shell import TimedPattern
from ..vfs import ShellError
from . import inputs, spec_format, spec_message, split_lines
from .text import CLASS_SYNTAX, _Errors, posix_regex_error, posix_to_python


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


SED_USAGE = COMMANDS["sed"].help  # spec/commands/sed.json; also printed after a usage error


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
            raise fail({"addr": spec_message("sed", "unterminatedAddress"), "y": spec_message("sed", "unterminatedY")}.get(what, spec_message("sed", "unterminatedS")), i)
        i += 1
        return "".join(out)

    def compile_rx(text: str, flags: int, error_at: int) -> re.Pattern:
        if text == "":
            if last_rx[0] is None:
                raise fail(spec_message("sed", "noPreviousRegex"), error_at)
            return last_rx[0]
        if (error := posix_regex_error(text, extended, grep=False)) is not None:
            raise ShellError(error, 4) if error == CLASS_SYNTAX else fail(error, error_at)
        try:
            rx = TimedPattern(re.compile(posix_to_python(text, extended), flags))
        except re.error as e:
            raise fail(spec_message("sed", "invalidRegex", detail=str(e)), error_at) from None
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

    def read_label(stop: str = ";\n}") -> str:
        nonlocal i
        skip_blanks()
        start = i
        while i < n and script[i] not in stop:
            i += 1
        return script[start:i].strip()

    def read_text() -> str:
        r"""Text for a/i/c: `a text` or `a\` NEWLINE text, where a trailing backslash continues the text."""
        nonlocal i
        skip_blanks()
        if i >= n:
            raise fail(spec_message("sed", "expectedBackslash"), i)
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
                raise fail(spec_message("sed", "unexpectedComma"), min(i + 1, n))
        if c.a1 == ("zero",) and (c.a2 is None or c.a2[0] != "re"):
            raise fail(spec_message("sed", "lineZero"), min(i + 1, n))
        skip_blanks()
        if i < n and script[i] == "!":
            c.negate = True
            i += 1
            skip_blanks()
            if i < n and script[i] == "!":
                raise fail(spec_message("sed", "multipleBang"), i + 1)
        if i >= n:
            raise fail(spec_message("sed", "missingCommand"), i)
        c.op = script[i]
        i += 1
        op = c.op
        at = i
        if op == "{":
            blocks.append((len(cmds), i))
        elif op == "}":
            if not blocks:
                raise fail(spec_message("sed", "unexpectedBrace"), i)
            if c.a1 is not None:
                raise fail(spec_message("sed", "braceAddresses"), i)
            cmds[blocks.pop()[0]].target = len(cmds) + 1
        elif op == "s":
            if i >= n:
                raise fail(spec_message("sed", "unterminatedS"), i)
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
                        raise fail(spec_message("sed", "multipleNumbers"), i)
                    if c.nth == 0:
                        raise fail(spec_message("sed", "zeroNumber"), i)
                    numbered = True
                    continue
                if f not in "gpiImMew":
                    break
                i += 1
                if f == "g" and c.glob:
                    raise fail(spec_message("sed", "multipleG"), i)
                if f == "p" and c.print:
                    raise fail(spec_message("sed", "multipleP"), i)
                if f == "g":
                    c.glob = True
                elif f == "p":
                    c.print = True
                elif f in "iI":
                    flags |= re.IGNORECASE
                elif f in "mM":
                    flags |= re.MULTILINE
                elif f == "w" and not script[i:].split("\n", 1)[0].strip():
                    raise fail(spec_message("sed", "missingFilename"), i)
                else:
                    raise fail(spec_message("sed", "flagNotSupported", flag=f), i)
            skip_blanks()
            if i < n and script[i] not in ";\n}#":
                raise fail(spec_message("sed", "unknownSOption"), i + 1)
            # GNU compiles the regex once it has read the character that ends the command
            compiled_at = i + 1 if i < n and script[i] in ";\n" else i
            c.rx = compile_rx(find, flags, compiled_at)
            refs = [p for p in c.repl if isinstance(p, int)]
            if refs and max(refs) > c.rx.groups:
                raise fail(spec_message("sed", "invalidReference", number=str(max(refs))), compiled_at)
        elif op == "y":
            delim = script[i] if i < n else ""
            i += 1
            src = unescape(read_delimited(delim, "y"))
            dst = unescape(read_delimited(delim, "y"))
            if len(src) != len(dst):
                raise fail(spec_message("sed", "yLengths"), i)
            c.table = {ord(a): b for a, b in zip(src, dst)}
        elif op in "aic":
            c.text = read_text()
        elif op == ":":
            if c.a1 is not None:
                raise fail(spec_message("sed", "colonAddresses"), i)
            label = read_label(";\n \t")  # a label ends at a blank too: `:x p` is the label x, then p
            if not label:
                raise fail(spec_message("sed", "colonLabel"), i)
            labels[label] = len(cmds)
        elif op in "btT":
            c.label = (read_label().split() or [""])[0]  # `bx p` jumps to x; what follows a blank is not read
        elif op in "rR":
            # the file name is the rest of the line, as in GNU sed
            skip_blanks()
            name_start = i
            while i < n and script[i] != "\n":
                i += 1
            c.text = script[name_start:i]
            if not c.text:
                raise fail(spec_message("sed", "missingFilename"), i)
        elif op == "v":
            read_label()  # "requires GNU sed VERSION": always met
        elif op in "qQ":
            skip_blanks()
            c.code = read_number() if i < n and script[i].isdigit() else 0
        else:
            if op in "wW" and not read_label():
                raise fail(spec_message("sed", "missingFilename"), i)
            if op not in "=dDgGhHnNpPxz":
                raise fail(spec_message("sed", "unknownCommand", op=op), at)
        cmds.append(c)
        skip_blanks()
        if op not in "{:rR" and i < n and script[i] not in ";\n}#":
            raise fail(spec_message("sed", "extraCharacters"), i + 1)
    if blocks:
        raise fail(spec_message("sed", "unmatchedBrace"), blocks[-1][1], char_zero=True)
    for c in cmds:
        if c.op in "btT":
            if c.label and c.label not in labels:
                raise ShellError(spec_message("sed", "missingLabel", label=c.label), 4)
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


def _sed_execute(cmds: list[_SedCmd], text: str, out, quiet: bool, ctx=None, read_file=lambda f: None) -> int | None:
    """Runs a parsed script over text. Returns the q/Q exit code if the script quit, else None.
    read_file: a file's text for r and R, or None when it can't be read (sed then adds nothing)."""
    r_lines: dict = {}  # R: each file's lines not yet used
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

    def write_appends(appends):  # a and R add a line; r adds a file as it is
        nonlocal pending_newline
        for t, raw in appends:
            if not raw:
                write(t)
                continue
            if pending_newline:
                out.write("\n")
                pending_newline = False
            out.write(t)
        appends.clear()

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
        appends: list[tuple[str, bool]] = []
        tflag = False
        autoprint = not quiet
        pc = 0
        while pc < len(cmds):
            steps += 1
            if steps > 10_000_000:
                raise ShellError(spec_message("sed", "tooManySteps"))
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
                write_appends(appends)
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
                appends.append((c.text, False))
            elif op == "r":
                content = read_file(c.text)
                if content:
                    appends.append((content, True))
            elif op == "R":
                if c.text not in r_lines:
                    whole = read_file(c.text)
                    r_lines[c.text] = iter(split_lines(whole) if whole is not None else [])
                line = next(r_lines[c.text], None)
                if line is not None:
                    appends.append((line, False))
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
        write_appends(appends)
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
            # a directory reads as an empty script, as GNU sed takes it
            text = ctx.stdin if value == "-" else "" if ctx.fs.is_dir(ctx.resolve(value)) else ctx.fs.read_text(ctx.resolve(value))
        except ShellError as e:
            raise ShellError(spec_message("sed", "cannotOpenScript", file=value, errno=str(e).rsplit(": ", 1)[-1]), 4) from None
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
                        return bad(spec_format(GETOPT["missingLongArgument"], option=name))
                    value = args[i]
                    i += 1
                add_script(name[0], value)
            elif name in ("posix", "debug", "sandbox", "follow-symlinks", "unbuffered"):
                pass
            elif name == "help":
                ctx.stdout.write(SED_USAGE)
                return 0
            else:
                return bad(spec_format(GETOPT["unrecognizedOption"], option=arg))
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
                        return bad(spec_format(GETOPT["missingShortArgument"], option=f))
                    value = args[i]
                    i += 1
                add_script(f, value)
                break
            else:
                return bad(spec_format(GETOPT["invalidOption"], option=f))
    if not scripts:
        if not files:
            return bad("")
        add_script("e", files.pop(0))
    if scripts[0][0].startswith("#n"):  # "#n" opening the script means -n
        quiet = True
    cmds = _sed_parse(scripts, extended)
    stdin_read = False

    def read_file(f):
        nonlocal stdin_read
        if f == "/dev/stdin":  # stdin is read once
            if stdin_read:
                return ""
            stdin_read = True
            return ctx.stdin
        try:
            path = ctx.resolve(f)
            return ctx.fs.read_text(path) if ctx.fs.is_file(path) else None
        except ShellError:
            return None

    if in_place:
        if not files:
            raise ShellError(spec_message("sed", "noInputFiles"), 1)
        code = 0
        for f in files:
            path = ctx.resolve(f)
            try:
                original = ctx.fs.read_text(path)
            except ShellError as e:
                if ctx.fs.is_dir(path):
                    ctx.error("sed: " + spec_message("sed", "notRegularFile", file=f))
                    code = 4
                else:
                    ctx.error("sed: " + spec_message("sed", "openFailed", file=f, errno=str(e).rsplit(": ", 1)[-1]))
                    code = 2
                continue
            buf = io.StringIO()
            quit_code = _sed_execute(cmds, original, buf, quiet, ctx, read_file)
            if suffix:
                backup = suffix.replace("*", f.rsplit("/", 1)[-1]) if "*" in suffix else f + suffix
                ctx.fs.write_text(ctx.resolve(backup), original)
            if ctx.fs.is_symlink(path):
                ctx.fs.delete(path)  # like GNU sed -i, the edited text replaces the link; its target stays as it was
            ctx.fs.write_text(path, buf.getvalue())
            if quit_code is not None:
                return quit_code
        return code
    errors = _Errors()
    texts = [t for _, t in inputs(ctx, files, errors)]
    if separate:
        for t in texts:
            quit_code = _sed_execute(cmds, t, ctx.stdout, quiet, ctx, read_file)
            if quit_code is not None:
                return quit_code
    else:
        joined = "".join(t if t.endswith("\n") or k == len(texts) - 1 else t + "\n" for k, t in enumerate(texts))
        quit_code = _sed_execute(cmds, joined, ctx.stdout, quiet, ctx, read_file)
        if quit_code is not None:
            return quit_code
    return 4 if any(f != "-" and ctx.fs.is_dir(ctx.resolve(f)) for f in files) else 2 if errors.hit else 0  # 4: a read error (a directory)
