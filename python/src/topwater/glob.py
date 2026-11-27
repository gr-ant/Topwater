"""Brace expansion and globbing. See docs/architecture.md."""
from __future__ import annotations

import functools
import re
import signal
import threading
import time
from contextvars import ContextVar
from typing import TYPE_CHECKING

from ._spec import DIAGNOSTICS
from .parser import Literal, Word
from .vfs import ShellError, normalize

if TYPE_CHECKING:
    from .shell import Shell


# ---------- brace expansion ----------

_MAX_BRACE = 10_000
_PLACEHOLDER = 0xE000


def brace_expand(w: Word) -> list[Word]:
    """Brace expansion: a{b,c}d, {1..10}, {01..05}, {a..e}, {0..10..2}; a brace may span an expansion: {$x,y}."""
    if not any(isinstance(p, Literal) and not p.quoted and "{" in p.text for p in w.parts):
        return [w]
    # everything but unquoted text becomes one placeholder character, so braces work on plain text
    text: list[str] = []
    opaque: list = []
    for p in w.parts:
        if isinstance(p, Literal) and not p.quoted:
            text.append(p.text)
        else:
            text.append(chr(_PLACEHOLDER + len(opaque)))
            opaque.append(p)
    if len(opaque) > 0x1000:
        return [w]
    joined = "".join(text)
    results = _brace_text(joined)
    if len(results) == 1 and results[0] == joined:
        return [w]
    return [_rebuild(t, opaque) for t in results]


def _rebuild(text: str, opaque: list) -> Word:
    parts: list = []
    lit: list[str] = []
    for c in text:
        k = ord(c) - _PLACEHOLDER
        if not 0 <= k < len(opaque):
            lit.append(c)
            continue
        if lit:
            parts.append(Literal("".join(lit), False))
            lit.clear()
        parts.append(opaque[k])
    if lit or not parts:
        parts.append(Literal("".join(lit), False))
    return Word(parts)


def _brace_text(text: str) -> list[str]:
    open_ = text.find("{")
    while open_ >= 0:
        close = _brace_close(text, open_)
        alts = _brace_alternatives(text[open_ + 1:close]) if close >= 0 else None
        if alts is not None:
            results: list[str] = []
            for alt in alts:
                results.extend(_brace_text(text[:open_] + alt + text[close + 1:]))
                if len(results) > _MAX_BRACE:
                    raise ShellError(DIAGNOSTICS["braceTooMany"])
            return results
        open_ = text.find("{", open_ + 1)
    return [text]


def _brace_close(s: str, open_: int) -> int:
    depth = 0
    for i in range(open_, len(s)):
        if s[i] == "{":
            depth += 1
        elif s[i] == "}":
            depth -= 1
            if depth == 0:
                return i
    return -1


def _brace_alternatives(inner: str) -> list[str] | None:
    m = re.fullmatch(r"(-?\d+)\.\.(-?\d+)(?:\.\.(-?\d+))?", inner)
    if m:
        a, b = int(m.group(1)), int(m.group(2))
        step = abs(int(m.group(3))) if m.group(3) else 1
        step = step or 1
        pad = any(len(g) > 1 and g.lstrip("-").startswith("0") for g in (m.group(1), m.group(2)))
        width = max(len(m.group(1)), len(m.group(2)))
        if abs(b - a) // step > _MAX_BRACE:
            raise ShellError(DIAGNOSTICS["braceTooMany"])
        rng = range(a, b + 1, step) if a <= b else range(a, b - 1, -step)
        return [str(v).zfill(width) if pad else str(v) for v in rng]
    m = re.fullmatch(r"([a-zA-Z])\.\.([a-zA-Z])(?:\.\.(-?\d+))?", inner)
    if m:
        a, b = ord(m.group(1)), ord(m.group(2))
        step = max(1, abs(int(m.group(3)))) if m.group(3) else 1
        return [chr(c) for c in (range(a, b + 1, step) if a <= b else range(a, b - 1, -step))]
    parts, depth, start = [], 0, 0
    for i, c in enumerate(inner):
        if c == "{":
            depth += 1
        elif c == "}":
            depth -= 1
        elif c == "," and depth == 0:
            parts.append(inner[start:i])
            start = i + 1
    parts.append(inner[start:])
    return parts if len(parts) >= 2 else None


# ---------- globbing ----------

def has_wildcard(s: str) -> bool:
    return any(c in s for c in "*?[") or "@(" in s or "+(" in s or "!(" in s


def glob_escape(s: str) -> str:
    return "".join("\\" + c if c in "*?[]\\()|@!+" else c for c in s)


def _has_unescaped_wildcard(seg: str) -> bool:
    i = 0
    while i < len(seg):
        if seg[i] == "\\":
            i += 2
            continue
        if seg[i] in "*?[" or (seg[i] in "@+!" and i + 1 < len(seg) and seg[i + 1] == "("):
            return True
        i += 1
    return False


_RUNNING: ContextVar["Shell | None"] = ContextVar("topwater_running_shell", default=None)  # whose call is running


class TimedPattern:
    """A compiled regex whose matching the call's time limit can stop. Python's re has no timeout but checks for signals
    while it backtracks, so each match runs under a one-shot SIGALRM for the time the call has left; the alarm is set
    only around the match and the host's handler and timer are put back after it.
    shortcut: off the main thread, without setitimer (Windows), or when the host uses SIGALRM itself, a match has no
    limit of its own (only the call's checks between commands); the `regex` module's timeout= would cover those."""
    __slots__ = ("_rx",)

    def __init__(self, rx: re.Pattern):
        self._rx = rx

    def __getattr__(self, name: str):
        return getattr(self._rx, name)

    def _run(self, fn, *args, **kwargs):
        from .shell.types import CallTimeout
        shell = _RUNNING.get()
        deadline = shell._deadline if shell is not None else float("inf")
        if (deadline == float("inf") or not hasattr(signal, "setitimer")
                or threading.current_thread() is not threading.main_thread()
                or signal.getsignal(signal.SIGALRM) not in (signal.SIG_DFL, None)
                or signal.getitimer(signal.ITIMER_REAL) != (0.0, 0.0)):
            return fn(*args, **kwargs)
        left = deadline - time.monotonic()
        if left <= 0:
            raise CallTimeout()
        active = [True]

        def expired(signum, frame):
            if active[0]:  # not once the match has returned
                raise CallTimeout()

        previous = signal.signal(signal.SIGALRM, expired)
        try:
            signal.setitimer(signal.ITIMER_REAL, left)
            return fn(*args, **kwargs)
        finally:
            active[0] = False
            signal.setitimer(signal.ITIMER_REAL, 0)
            signal.signal(signal.SIGALRM, previous if previous is not None else signal.SIG_DFL)

    def search(self, *a, **k):
        return self._run(self._rx.search, *a, **k)

    def match(self, *a, **k):
        return self._run(self._rx.match, *a, **k)

    def fullmatch(self, *a, **k):
        return self._run(self._rx.fullmatch, *a, **k)

    def sub(self, *a, **k):
        return self._run(self._rx.sub, *a, **k)

    def subn(self, *a, **k):
        return self._run(self._rx.subn, *a, **k)

    def split(self, *a, **k):
        return self._run(self._rx.split, *a, **k)

    def findall(self, *a, **k):
        return self._run(self._rx.findall, *a, **k)

    def finditer(self, *a, **k):
        it = self._rx.finditer(*a, **k)
        while (m := self._run(next, it, None)) is not None:
            yield m


@functools.lru_cache(maxsize=1024)
def _cached(pattern: str, flags: int) -> re.Pattern:
    """A compiled-once regex: patterns repeat (a case in a loop), so building one per match would dominate."""
    return re.compile(pattern, flags)


def glob_to_regex_body(pattern: str, slashes: bool = False) -> str:
    out: list[str] = []
    _glob_body(pattern, 0, out, slashes, False)
    return "".join(out)


def _glob_body(p: str, i: int, out: list, slashes: bool, in_group: bool) -> int:
    """Translates pattern from i, stopping at an unescaped | or ) when inside an extglob group. Returns the stop index."""
    any_ = "." if slashes else "[^/]"
    while i < len(p):
        c = p[i]
        if in_group and c in "|)":
            return i
        if c in "@!*+?" and i + 1 < len(p) and p[i + 1] == "(" and _group_end(p, i + 1) >= 0:
            # extglob: @(a|b) one of, *(..) any number, +(..) at least one, ?(..) optional, !(..) anything else
            i += 2
            alternatives = []
            while True:
                alt: list[str] = []
                i = _glob_body(p, i, alt, slashes, True)
                alternatives.append("".join(alt))
                if i >= len(p) or p[i] == ")":
                    break
                i += 1  # past '|'
            group = "(?:" + "|".join(alternatives) + ")"
            # shortcut: !(..) is exact only at the end of a pattern (rm !(*.txt)); make it a real complement if agents nest it
            out.append({"@": group, "*": group + "*", "+": group + "+", "?": group + "?"}.get(c, f"(?:(?!{group}\\Z){any_}*)"))
            i += 1
            continue
        if c == "\\" and i + 1 < len(p):
            i += 1
            out.append(re.escape(p[i]))
        elif c == "*":
            out.append(any_ + "*")
        elif c == "?":
            out.append(any_)
        elif c == "[" and _bracket_end(p, i) > 0:
            close = _bracket_end(p, i)
            out.append(_bracket(p[i + 1:close]))
            i = close
        else:
            out.append(re.escape(c))
        i += 1
    return i


def _group_end(p: str, i: int) -> int:
    depth = 0
    while i < len(p):
        if p[i] == "\\":
            i += 2
            continue
        if p[i] == "(":
            depth += 1
        elif p[i] == ")":
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return -1


def _bracket_end(p: str, i: int) -> int:
    """The ']' closing the bracket expression that opens at i (allowing []...] and [[:class:]]), or -1."""
    k = i + 1
    if k < len(p) and p[k] in "!^":
        k += 1
    if k < len(p) and p[k] == "]":
        k += 1
    while k < len(p):
        if p[k] == "[" and k + 1 < len(p) and p[k + 1] in ":.=":
            e = p.find(p[k + 1] + "]", k + 2)
            if e < 0:
                return -1
            k = e + 2
            continue
        if p[k] == "\\":
            k += 2
            continue
        if p[k] == "]":
            return k
        k += 1
    return -1


def _bracket(inner: str) -> str:
    from .commands.text import _CLASSES
    out = ["["]
    k = 0
    if inner and inner[0] in "!^":
        out.append("^")
        k = 1
    while k < len(inner):
        c = inner[k]
        if c == "[" and k + 1 < len(inner) and inner[k + 1] == ":":
            e = inner.find(":]", k + 2)
            if e >= 0:
                out.append(_CLASSES.get(inner[k + 2:e], ""))
                k = e + 2
                continue
        if c == "\\" and k + 1 < len(inner):
            k += 1
            c = inner[k]
        if c in "\\[]^":
            out.append("\\")
        out.append(c)
        k += 1
    out.append("]")
    return "".join(out)


def glob_to_regex(pattern: str, ignore_case: bool = False, slashes: bool = False) -> "TimedPattern":
    """slashes=True for case/[[ ]]/${x%pat} matching, where * also matches '/'. Timed: a pattern like *a*a*a*b
    backtracks for ages on a long name, and the call's time limit must still stop it."""
    return TimedPattern(_cached("^" + glob_to_regex_body(pattern, slashes) + r"\Z", (re.IGNORECASE if ignore_case else 0) | re.S))


def _join(display: str, name: str) -> str:
    return name if display == "" else "/" + name if display == "/" else display + "/" + name


def glob_expand(shell: Shell, pattern: str) -> list[str]:
    """Matches a glob against the virtual filesystem; returns paths in the same form (relative/absolute) as the pattern."""
    absolute = pattern.startswith("/")
    current = [("/" if absolute else shell.cwd, "/" if absolute else "")]
    dotglob, globstar = "dotglob" in shell.shopts, "globstar" in shell.shopts
    nocase = "nocaseglob" in shell.shopts
    segs = [s for s in pattern.split("/") if s]
    for k, seg in enumerate(segs):
        nxt = []
        if seg == "**" and globstar:  # zero or more directories; as the last segment, every file and directory below
            last = not pattern.endswith("/") and k == len(segs) - 1
            stack = list(current)
            while stack:
                abs_, display = stack.pop(0)
                is_base = (abs_, display) in current
                if not last:
                    nxt.append((abs_, display))
                elif display and is_base:
                    nxt.append((abs_, _join(display, "")))  # bash lists the base itself as "dir/"
                if shell.fs.is_dir(abs_) and shell.fs.can_read(abs_):
                    for name in shell.fs.listdir(abs_):
                        child = normalize(abs_, name)
                        if dotglob or not name.startswith("."):
                            if shell.fs.is_dir(child, follow=False):  # like bash, ** does not follow links
                                stack.append((child, _join(display, name)))
                            elif last:
                                nxt.append((child, _join(display, name)))
                    if last and not is_base:
                        nxt.append((abs_, display))
            current = nxt
            continue
        for abs_, display in current:
            if not shell.fs.is_dir(abs_) or not shell.fs.can_read(abs_):
                continue
            if seg in (".", ".."):
                nxt.append((normalize(abs_, seg), _join(display, seg)))
            elif _has_unescaped_wildcard(seg):
                rx = glob_to_regex(seg, nocase)
                for name in shell.fs.listdir(abs_):
                    if rx.match(name) and (not name.startswith(".") or seg.startswith(".") or dotglob):
                        nxt.append((normalize(abs_, name), _join(display, name)))
            else:
                name = re.sub(r"\\(.)", r"\1", seg)
                child = normalize(abs_, name)
                if shell.fs.exists(child, follow=False):
                    nxt.append((child, _join(display, name)))
        current = nxt
    if pattern.endswith("/"):  # `echo */` lists directories only
        return sorted(_join(d, "") if d != "/" else d for a, d in current if d and shell.fs.is_dir(a))
    return sorted(d for _, d in current if d)
