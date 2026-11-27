"""printf, strftime, shell quoting and backslash escapes. See docs/architecture.md."""
from __future__ import annotations

import re
from datetime import datetime, timezone

from . import bytetext


_ESCAPES = {"n": "\n", "t": "\t", "r": "\r", "\\": "\\", "a": "\a", "b": "\b", "f": "\f", "v": "\v", "e": "\x1b", '"': '"'}


def _byte_char(b: int) -> str:
    """A byte value as a character: ASCII as is, 0x80-0xff as the escaped raw byte (bytetext), so `printf '\\xd5'` writes one byte."""
    return chr(b) if b < 0x80 else chr(0xDC00 + b)


def unescape(s: str, keep_unknown: bool = False, echo: bool = False) -> str:
    """Backslash escapes: \\n \\t ..., \\xHH, and octal (\\NNN, or \\0NNN for echo -e)."""
    out = []
    i = 0
    while i < len(s):
        if s[i] != "\\" or i + 1 >= len(s):
            out.append(s[i])
            i += 1
            continue
        n = s[i + 1]
        if n in "01234567" and (not echo or n == "0"):
            j = i + 2 if echo else i + 1
            k = j
            while k < len(s) and k < j + 3 and s[k] in "01234567":
                k += 1
            out.append(_byte_char(int(s[j:k] or "0", 8) & 0xFF))
            i = k
        elif n == "x" and i + 2 < len(s) and s[i + 2] in "0123456789abcdefABCDEF":
            k = i + 2
            while k < len(s) and k < i + 4 and s[k] in "0123456789abcdefABCDEF":
                k += 1
            out.append(_byte_char(int(s[i + 2:k], 16)))
            i = k
        else:
            out.append(_ESCAPES.get(n, ("\\" + n) if keep_unknown else n))
            i += 2
    return bytetext.rejoin("".join(out))


_PRINTF_SPEC = re.compile(r"%([-0 +#]*)(\*|\d*)(?:\.(\*|\d*))?(?:\(([^)]*)\))?([sdiufxXoceEgGbqT%])")


_SHELL_SAFE = re.compile(r"[A-Za-z0-9_./+:,=@%^-]+")


def _c_integer(s: str) -> tuple[int, bool]:
    """Parses a printf integer argument like bash: 0x1f, 017 (octal), 'c (character code). Returns (value, valid)."""
    t = s.strip()
    if t[:1] in ("'", '"'):
        return (ord(t[1]) if len(t) > 1 else 0), True
    m = re.match(r"[+-]?(0[xX][0-9a-fA-F]+|0[0-7]*|[1-9][0-9]*)", t)
    if not m:
        return 0, t == ""
    body = m.group(1)
    v = int(body, 16) if body[:2].lower() == "0x" else int(body, 8) if body.startswith("0") else int(body)
    return (-v if t.startswith("-") else v), m.end() == len(t)


def _c_float(s: str) -> tuple[float, bool]:
    t = s.strip()
    m = re.match(r"[+-]?(\d+\.?\d*([eE][+-]?\d+)?|\.\d+([eE][+-]?\d+)?|inf|nan)", t, re.I)
    if not m:
        return 0.0, t == ""
    return float(m.group(0)), m.end() == len(t)


def shell_quote(s: str) -> str:
    """printf %q: quotes a string so bash would read it back as one word."""
    if s == "":
        return "''"
    if _SHELL_SAFE.fullmatch(s):
        return s
    if any(ord(c) < 32 or ord(c) == 127 for c in s):
        body = s.replace("\\", "\\\\").replace("'", "\\'")
        for c, e in (("\n", "\\n"), ("\t", "\\t"), ("\r", "\\r"), ("\x1b", "\\E")):
            body = body.replace(c, e)
        return "$'" + re.sub(r"[\x00-\x1f\x7f]", lambda m: f"\\{ord(m.group(0)):03o}", body) + "'"
    return re.sub(r"([^A-Za-z0-9_./+:,=@%^\x80-\U0010ffff-])", r"\\\1", s)


def printf_format(fmt: str, args: list[str], errors: list[str] | None = None) -> str:
    """bash printf: C conversions, the format is reused while arguments remain; bad numbers are reported in errors."""
    fmt = unescape(fmt, keep_unknown=True)
    out = []
    nxt = 0
    stop = False  # a \c in a %b argument ends all output, the rest of the format included
    mark = "\ue001"

    def take() -> str:
        nonlocal nxt
        arg = args[nxt] if nxt < len(args) else ""
        nxt += 1
        return arg

    def sub(m: re.Match) -> str:
        nonlocal stop
        flags, width, prec, tfmt, conv = m.group(1), m.group(2), m.group(3), m.group(4), m.group(5)
        if conv == "%":
            return "%"
        if conv == "u":
            conv = "d"
        if (tfmt is not None) != (conv == "T"):
            return m.group(0)
        if width == "*":
            w, _ = _c_integer(take())
            flags, width = (flags + "-", str(-w)) if w < 0 else (flags, str(w))
        if prec == "*":
            prec = str(max(0, _c_integer(take())[0]))
        # a huge width or precision is capped, as the C# port does, so `printf %0999999999d` can't exhaust memory
        width = str(min(int(width), 10_000)) if width else width
        prec = str(min(int(prec), 10_000)) if prec else prec
        arg = take()
        spec = "%" + flags + width + ("." + (prec or "0") if prec is not None else "")
        if conv in "sbqcT" and stop:
            return ""
        if conv == "b" and "\\c" in arg:
            stop = True
            return unescape(arg[:arg.index("\\c")], keep_unknown=True, echo=True) + mark
        if conv == "T":
            # %(fmt)T: a time in seconds since the epoch; -1 or nothing is now (UTC, like the rest of this machine)
            from datetime import datetime, timedelta, timezone
            when = datetime.now(timezone.utc) if arg in ("", "-1", "-2") else datetime(1970, 1, 1, tzinfo=timezone.utc) + timedelta(seconds=_c_integer(arg)[0])
            return (spec + "s") % strftime(when.replace(tzinfo=None), tfmt)
        if conv in "sbq":
            text = unescape(arg, keep_unknown=True, echo=True) if conv == "b" else shell_quote(arg) if conv == "q" else arg
            return (spec + "s") % text
        if conv == "c":
            return (spec + "s") % arg[:1]
        if conv in "dioxX":
            v, ok = _c_integer(arg)
            if not ok and errors is not None:
                errors.append(f"printf: {arg}: invalid number")
            if conv == "o" and "#" in flags:  # C's %#o is a leading 0, Python's is 0o
                return (spec.replace("#", "") + "s") % ("0" + format(v, "o") if v else "0")
            return (spec + conv.replace("i", "d")) % v
        v, ok = _c_float(arg)
        if not ok and errors is not None:
            errors.append(f"printf: {arg}: invalid number")
        return (spec + conv) % v

    while True:
        before = nxt
        out.append(_PRINTF_SPEC.sub(sub, fmt))
        if stop:
            out[-1] = out[-1][:out[-1].index(mark)]
            break
        if nxt == before or nxt >= len(args):
            break
    return "".join(out)


_DAYS = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday"]


_MONTHS = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November",
           "December"]


def strftime(t: datetime, fmt: str) -> str:
    """GNU date's format directives (the common ones, with the - flag for no padding). Mirrored in C#."""
    out = []
    i = 0
    while i < len(fmt):
        c = fmt[i]
        if c != "%" or i + 1 >= len(fmt):
            out.append(c)
            i += 1
            continue
        # %:z +hh:mm, %::z +hh:mm:ss, %:::z +hh (the clock is UTC)
        colons = 0
        while i + 1 + colons < len(fmt) and fmt[i + 1 + colons] == ":":
            colons += 1
        if 0 < colons <= 3 and i + 1 + colons < len(fmt) and fmt[i + 1 + colons] == "z":
            out.append({1: "+00:00", 2: "+00:00:00"}.get(colons, "+00"))
            i += colons + 2
            continue
        no_pad = fmt[i + 1] == "-" and i + 2 < len(fmt)
        d = fmt[i + 2] if no_pad else fmt[i + 1]
        i += 3 if no_pad else 2
        hour12 = t.hour % 12 or 12
        num = {"Y": (t.year, 4), "m": (t.month, 2), "d": (t.day, 2), "e": (t.day, -2), "H": (t.hour, 2), "k": (t.hour, -2),
               "I": (hour12, 2), "l": (hour12, -2), "M": (t.minute, 2), "S": (t.second, 2), "y": (t.year % 100, 2),
               "j": (t.timetuple().tm_yday, 3), "u": (t.isoweekday(), 1), "w": (t.isoweekday() % 7, 1)}
        if d == "c":
            out.append(strftime(t, "%a %b %e %H:%M:%S %Y"))
            continue
        if d in num:
            v, width = num[d]
            out.append(str(v) if no_pad else str(v).rjust(abs(width), "0" if width > 0 else " "))
            continue
        out.append({
            "F": f"{t.year:04}-{t.month:02}-{t.day:02}", "T": f"{t.hour:02}:{t.minute:02}:{t.second:02}",
            "D": f"{t.month:02}/{t.day:02}/{t.year % 100:02}", "R": f"{t.hour:02}:{t.minute:02}",
            "a": _DAYS[t.weekday()][:3], "A": _DAYS[t.weekday()], "b": _MONTHS[t.month - 1][:3], "h": _MONTHS[t.month - 1][:3],
            "B": _MONTHS[t.month - 1], "p": "AM" if t.hour < 12 else "PM", "s": str(int((t if t.tzinfo else t.replace(tzinfo=timezone.utc)).timestamp())),
            "N": f"{t.microsecond * 1000:09}", "Z": "UTC", "z": "+0000", "n": "\n", "t": "\t", "%": "%",
        }.get(d, "%" + ("-" if no_pad else "") + d))
    return "".join(out)
