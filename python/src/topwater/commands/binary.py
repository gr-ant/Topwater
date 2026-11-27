"""Binary and checksums: cmp, xxd, hexdump, od, strings, base64, md5sum, sha1sum, sha256sum, sha512sum. They read true bytes. See docs/architecture.md."""
from __future__ import annotations

import base64
import hashlib
import re

from .. import bytetext
from .._spec import COMMANDS
from ..vfs import ShellError, UsageException
from . import Args, inputs, spec_message
from .text import _Errors


def _num(s: str) -> int:
    try:
        return int(s, 16) if s.lower().startswith("0x") else int(s)
    except ValueError:
        raise ShellError(spec_message("xxd", "invalidNumber", value=s)) from None


def _read(ctx, files, errors) -> bytes:
    return b"".join(d for _, d in inputs(ctx, files, errors, raw=True))


def _slice(data: bytes, start: int, length: int | None = None) -> bytes:
    """A negative start counts from the end; a negative length means none."""
    data = data[start:]
    return data if length is None else data[:max(0, length)]


def _ascii(chunk: bytes) -> str:
    return "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)


def _xxd(ctx):
    a = Args.parse(ctx.args, COMMANDS["xxd"])
    errors = _Errors()
    data = _read(ctx, a.positional[:1], errors)  # a second operand is xxd's outfile; not supported
    start = _num(a.value("s") or "0")
    start = max(0, start + len(data) if start < 0 else start)
    data = _slice(data, start, _num(a.value("l")) if a.value("l") is not None else None)
    cols = _num(a.value("c") or ("30" if a.has("p") else "16"))
    if cols <= 0:
        raise ShellError(spec_message("xxd", "invalidColumns"))
    if a.has("p"):
        for i in range(0, len(data), cols):
            ctx.print(data[i:i + cols].hex())
        return 1 if errors.hit else 0
    for i in range(0, len(data), cols):
        chunk = data[i:i + cols]
        hexpart = "".join(f"{b:02x}" + (" " if k % 2 else "") for k, b in enumerate(chunk))
        ctx.print(f"{start + i:08x}: {hexpart:<{cols * 2 + cols // 2}} {_ascii(chunk)}")
    return 1 if errors.hit else 0


def _squeezed(data: bytes, width: int):
    """Yields (offset, chunk) per line, with None for a "*" line where lines repeat (hexdump/od style)."""
    prev, starred = None, False
    for i in range(0, len(data), width):
        chunk = data[i:i + width]
        if chunk == prev and len(chunk) == width:
            if not starred:
                yield i, None
                starred = True
            continue
        prev, starred = chunk, False
        yield i, chunk


def _hexdump(ctx):
    a = Args.parse(ctx.args, COMMANDS["hexdump"])
    errors = _Errors()
    data = _slice(_read(ctx, a.positional, errors), _num(a.value("s") or "0"), _num(a.value("n")) if a.value("n") is not None else None)
    for i, chunk in (((i, data[i:i + 16]) for i in range(0, len(data), 16)) if a.has("v") else _squeezed(data, 16)):
        if chunk is None:
            ctx.print("*")
            continue
        hexpart = "".join(f"{b:02x} " + (" " if k == 7 else "") for k, b in enumerate(chunk))
        ctx.print(f"{i:08x}  {hexpart:<49} |{_ascii(chunk)}|")
    if data:
        ctx.print(f"{len(data):08x}")
    return 1 if errors.hit else 0


_OD_ESCAPES = {0: "\\0", 7: "\\a", 8: "\\b", 9: "\\t", 10: "\\n", 11: "\\v", 12: "\\f", 13: "\\r"}


def _od(ctx):
    a = Args.parse(ctx.args, COMMANDS["od"])
    failed = []
    data = _slice(_read(ctx, a.positional, lambda: failed.append(1)), _num(a.value("j") or "0"), _num(a.value("N")) if a.value("N") is not None else None)
    if failed and len(failed) == len(a.positional):
        return 1  # no input could be read: GNU od prints no offset either
    fmt = a.value("t") or ("c" if a.has("c") else "x2" if a.has("x") else "o2")
    fmt = {"x": "x4", "o": "o4"}.get(fmt, fmt)
    radix = a.value("A") or "o"
    if fmt == "c":
        def cells(chunk):
            return "".join(f"{_OD_ESCAPES.get(b) or (chr(b) if 32 <= b < 127 else f'{b:03o}'):>4}" for b in chunk)
    elif fmt in ("x1", "x2", "x4", "x8", "o1", "o2", "o4", "o8"):
        size = int(fmt[1])
        digits = size * 2 if fmt[0] == "x" else (size * 8 + 2) // 3

        def cells(chunk):
            chunk += b"\0" * (-len(chunk) % size)  # od pads a partial last word with zero bytes
            words = (int.from_bytes(chunk[k:k + size], "little") for k in range(0, len(chunk), size))
            return "".join(f" {w:0{digits}{fmt[0]}}" for w in words)
    else:
        raise ShellError(spec_message("od", "invalidType", type=fmt))
    if radix not in ("o", "x", "d", "n"):
        raise ShellError(spec_message("od", "invalidRadix", radix=radix))

    def addr(n):
        return "" if radix == "n" else f"{n:07o}" if radix == "o" else f"{n:06x}" if radix == "x" else f"{n:07d}"

    lines = ((i, data[i:i + 16]) for i in range(0, len(data), 16)) if a.has("v") else _squeezed(data, 16)
    for i, chunk in lines:
        ctx.print("*" if chunk is None else addr(i) + cells(chunk))
    if radix != "n":
        ctx.print(addr(len(data)))
    return 1 if failed else 0


def _visible(b: int) -> str:
    """A byte as cmp -b shows it: printable as itself, control bytes as ^X, high bytes as M- and the low byte's form."""
    return "M-" + _visible(b - 128) if b >= 128 else "^" + chr(b + 64) if b < 32 else "^?" if b == 127 else chr(b)


def _cmp(ctx):
    a = Args.parse(ctx.args, COMMANDS["cmp"])
    ops = a.positional
    if not ops:
        raise UsageException(spec_message("cmp", "missingOperand"))
    if len(ops) > 4:
        raise UsageException(spec_message("cmp", "extraOperand", operand=ops[4]))

    def number(s, option):
        if s is None:
            return 0
        if re.fullmatch(r"\s*\+?\d+\s*", s) or re.fullmatch(r"\s*-0+\s*", s):
            return int(s)
        raise UsageException(spec_message("cmp", "invalidNumber", option=option, value=s))

    i_value = a.value("i")
    skips = (i_value or "").split(":")
    skip1 = number(ops[2], "ignore-initial") if len(ops) > 2 else number(None if i_value is None else skips[0], "ignore-initial")
    skip2 = number(ops[3], "ignore-initial") if len(ops) > 3 else number(None if i_value is None else skips[-1], "ignore-initial")
    limit = number(a.value("n"), "bytes") if a.value("n") is not None else None
    silent, verbose, show = a.has("s"), a.has("l"), a.has("b")
    names = [ops[0], ops[1] if len(ops) > 1 else "-"]
    data = [d for _, d in inputs(ctx, names, lambda: None, raw=True, quiet=silent)]
    if len(data) < 2:
        return 2
    x, y = data[0][skip1:], data[1][skip2:]
    common = min(len(x), len(y))
    end = min(common, limit) if limit is not None else common
    # shortcut: -l pads positions to the digits of the compared length, which matches GNU for ordinary files
    longest = max(len(x), len(y))
    width = max(1, len(str(min(longest, limit) if limit is not None else longest)))
    line, differ = 1, False
    for i in range(end):
        if x[i] == y[i]:
            if x[i] == 10:
                line += 1
            continue
        differ = True
        if silent:
            return 1
        if not verbose:
            if show:
                ctx.print(spec_message("cmp", "differBytes", file1=names[0], file2=names[1], byte=str(i + 1), line=str(line),
                                       byte1=f"{x[i]:3o} {_visible(x[i])}", byte2=f"{y[i]:3o} {_visible(y[i])}"))
            else:
                ctx.print(spec_message("cmp", "differ", file1=names[0], file2=names[1], byte=str(i + 1), line=str(line)))
            return 1
        pos = str(i + 1).rjust(width)
        ctx.print(f"{pos} {x[i]:3o} {_visible(x[i]):<4} {y[i]:3o} {_visible(y[i])}" if show else f"{pos} {x[i]:3o} {y[i]:3o}")
    if len(x) == len(y) or (limit is not None and end >= limit):
        return 1 if differ else 0
    if not silent:
        short_name, shorter = (names[0], x) if len(x) < len(y) else (names[1], y)
        newlines = shorter.count(10)
        if not shorter:
            message = spec_message("cmp", "eofEmpty", file=short_name)
        elif verbose:
            message = spec_message("cmp", "eofVerbose", file=short_name, byte=str(len(shorter)))
        elif shorter[-1] == 10:
            message = spec_message("cmp", "eof", file=short_name, byte=str(len(shorter)), line=str(newlines))
        else:
            message = spec_message("cmp", "eofInLine", file=short_name, byte=str(len(shorter)), line=str(newlines + 1))
        ctx.error("cmp: " + message)
    return 1


def _strings(ctx):
    a = Args.parse(Args.numeric_shorthand(ctx.args), COMMANDS["strings"])
    errors = _Errors()
    minimum = _num(a.value("n") or "4")
    for _, data in inputs(ctx, a.positional, errors, raw=True):
        run = bytearray()
        for b in data + b"\0":
            if 32 <= b < 127 or b == 9:
                run.append(b)
                continue
            if len(run) >= minimum:
                ctx.print(run.decode("ascii"))
            run.clear()
    return 1 if errors.hit else 0


def _base64(ctx):
    a = Args.parse(ctx.args, COMMANDS["base64"])
    errors = _Errors()
    data = b"".join(t for _, t in inputs(ctx, a.positional, errors, raw=True))
    if a.has("d"):
        try:
            ctx.stdout.write(bytetext.decode(base64.b64decode(b"".join(data.split()), validate=True)))
        except ValueError:
            raise ShellError("invalid input") from None
        return 1 if errors.hit else 0
    enc = base64.b64encode(data).decode()
    wrap = int(a.value("w") or 76)
    if wrap > 0:
        enc = "\n".join(enc[i:i + wrap] for i in range(0, len(enc), wrap))
    ctx.print(enc)
    return 1 if errors.hit else 0


def _hasher(algo):
    def run(ctx):
        files = [x for x in ctx.args if not x.startswith("-") or x == "-"]
        errors = _Errors()
        for name, data in inputs(ctx, files, errors, raw=True):
            ctx.print(f"{hashlib.new(algo, data).hexdigest()}  {name if files else '-'}")
        return 1 if errors.hit else 0
    return run


def register(shell) -> None:
    r = shell.register_command
    r("cmp", _cmp)
    r("xxd", _xxd)
    r("hexdump", _hexdump)
    r("od", _od)
    r("strings", _strings)
    r("base64", _base64)
    for name, algo in (("md5sum", "md5"), ("sha1sum", "sha1"), ("sha256sum", "sha256"), ("sha512sum", "sha512")):
        r(name, _hasher(algo))
