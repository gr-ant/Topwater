"""Binary inspection: xxd, hexdump, od, strings. All of them read true bytes (text files: their UTF-8)."""
from __future__ import annotations

from ..vfs import ShellError
from . import Args, inputs
from .text import _Errors


def _num(s: str) -> int:
    try:
        return int(s, 16) if s.lower().startswith("0x") else int(s)
    except ValueError:
        raise ShellError(f"invalid number '{s}'") from None


def _read(ctx, files, errors) -> bytes:
    return b"".join(d for _, d in inputs(ctx, files, errors, raw=True))


def _slice(data: bytes, start: int, length: int | None = None) -> bytes:
    """A negative start counts from the end; a negative length means none."""
    data = data[start:]
    return data if length is None else data[:max(0, length)]


def _ascii(chunk: bytes) -> str:
    return "".join(chr(b) if 32 <= b < 127 else "." for b in chunk)


def _xxd(ctx):
    a = Args.parse(ctx.args, "p", "lsc", "plain=p,len=l,seek=s,cols=c")
    errors = _Errors()
    data = _read(ctx, a.positional[:1], errors)  # a second operand is xxd's outfile; not supported
    start = _num(a.value("s") or "0")
    start = max(0, start + len(data) if start < 0 else start)
    data = _slice(data, start, _num(a.value("l")) if a.value("l") is not None else None)
    cols = _num(a.value("c") or ("30" if a.has("p") else "16"))
    if cols <= 0:
        raise ShellError("invalid number of columns")
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
    a = Args.parse(ctx.args, "Cv", "ns", "canonical=C,length=n,skip=s,no-squeezing=v")
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
    a = Args.parse(ctx.args, "cxov", "ANtj", "address-radix=A,read-bytes=N,format=t,skip-bytes=j")
    errors = _Errors()
    data = _slice(_read(ctx, a.positional, errors), _num(a.value("j") or "0"), _num(a.value("N")) if a.value("N") is not None else None)
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
        raise ShellError(f"invalid type string '{fmt}' (supported here: c, x1, x2, x4, x8, o1, o2, o4, o8)")
    if radix not in ("o", "x", "d", "n"):
        raise ShellError(f"invalid output address radix '{radix}'; it must be one character from [doxn]")

    def addr(n):
        return "" if radix == "n" else f"{n:07o}" if radix == "o" else f"{n:06x}" if radix == "x" else f"{n:07d}"

    lines = ((i, data[i:i + 16]) for i in range(0, len(data), 16)) if a.has("v") else _squeezed(data, 16)
    for i, chunk in lines:
        ctx.print("*" if chunk is None else addr(i) + cells(chunk))
    if radix != "n":
        ctx.print(addr(len(data)))
    return 1 if errors.hit else 0


def _strings(ctx):
    a = Args.parse(Args.numeric_shorthand(ctx.args), "a", "n", "bytes=n,all=a")
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


def register(shell) -> None:
    r = shell.register_command
    r("xxd", _xxd, "Make a hex dump (-p plain, -l LEN, -s OFFSET)")
    r("hexdump", _hexdump, "Display file contents in hexadecimal (-C canonical, -n LEN)")
    r("od", _od, "Dump files in octal and other formats (-c, -x, -t x1, -An, -N LEN)")
    r("strings", _strings, "Print printable character sequences in files (-n MIN)")
