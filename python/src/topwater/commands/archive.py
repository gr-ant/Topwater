"""tar, gzip/gunzip/zcat, zip/unzip over the virtual filesystem. Archive bytes cross pipes and redirects as bytetext.

Archives are untrusted: extraction never leaves the target directory (no `..`, absolute names are made relative, nothing is
written under or hard-linked through a symbolic link the archive made, hard links only reach entries already there), and every extraction or
decompression stops with an error past the shell's max_extract_bytes / max_extract_entries (64 MB / 100,000 by default).
The tar format is read and written here; gzip uses zlib for the deflate stream and zip uses zipfile for the container,
but names, paths, limits and messages are all this module's.
"""
from __future__ import annotations

import io
import re
import struct
import zipfile
import zlib
from dataclasses import dataclass
from datetime import datetime, timezone

from .. import bytetext
from ..shell import glob_to_regex
from .._spec import COMMANDS
from ..vfs import ShellError, format_mode, get_parent, normalize
from . import Args, spec_message

_UTC = timezone.utc


class _Budget:
    """What one extraction or decompression may still produce."""

    def __init__(self, shell, exit_code: int):
        self.max_bytes = getattr(shell, "max_extract_bytes", 64 << 20)
        self.max_entries = getattr(shell, "max_extract_entries", 100_000)
        self._bytes, self._entries, self.exit_code = self.max_bytes, self.max_entries, exit_code

    def entry(self) -> None:
        self._entries -= 1
        if self._entries < 0:
            raise ShellError(spec_message("tar", "entryLimit", limit=str(self.max_entries)), self.exit_code)

    def charge(self, n: int) -> None:
        self._bytes -= n
        if self._bytes < 0:
            raise ShellError(spec_message("tar", "byteLimit", limit=str(self.max_bytes)), self.exit_code)

    def read(self, stream) -> bytes:
        out = []
        while chunk := stream.read(1 << 16):
            self.charge(len(chunk))
            out.append(chunk)
        return b"".join(out)


def _reason(e: Exception) -> str:
    return str(e).rsplit(": ", 1)[-1]


def _why_missing(fs, path: str) -> str:
    parent = get_parent(path)
    while parent != "/" and not fs.exists(parent):
        parent = get_parent(parent)
    return "Not a directory" if fs.is_file(parent) else "No such file or directory"


def _write(ctx, data: bytes) -> None:
    ctx.stdout.write(bytetext.decode(data))


def _utc_naive(ts: float) -> datetime:
    return datetime.fromtimestamp(ts, _UTC).replace(tzinfo=None)


# ---------- gzip ----------

def _is_gzip(d: bytes) -> bool:
    return d[:2] == b"\x1f\x8b"


def _gzip(data: bytes, level: int = 6) -> bytes:
    z = zlib.compressobj(level, zlib.DEFLATED, 31)
    return z.compress(data) + z.flush()


def _gunzip(data: bytes, budget: _Budget) -> tuple[bytes | None, str]:
    """Decompresses gzip data (all members); (None, gzip's wording) if it's corrupt or cut off."""
    out: list[bytes] = []
    rest = data
    try:
        while rest:
            if not _is_gzip(rest):
                break  # trailing garbage after the last member is ignored
            d = zlib.decompressobj(31)
            buf = rest
            while True:
                piece = d.decompress(buf, 1 << 16)
                budget.charge(len(piece))
                out.append(piece)
                buf = d.unconsumed_tail
                if d.eof:
                    break
                if not piece and not buf:
                    return None, "unexpected end of file"
            rest = d.unused_data
    except zlib.error:
        return None, "invalid compressed data--format violated"
    return b"".join(out), ""


def _gzip_overhead(d: bytes) -> int:
    """Header plus trailer bytes of a gzip member, which gzip leaves out of its ratio."""
    flags = d[3] if len(d) > 3 else 0
    i = 10
    if flags & 4 and i + 2 <= len(d):
        i += 2 + (d[i] | d[i + 1] << 8)
    if flags & 8:
        i = (d.find(b"\0", min(i, len(d))) + 1) or 0
    if flags & 16 and i > 0:
        i = (d.find(b"\0", min(i, len(d))) + 1) or 0
    if flags & 2:
        i += 2
    return max(i, 10) + 8


def _ratio(compressed: int, uncompressed: int) -> str:
    return "0.0" if uncompressed == 0 else f"{(uncompressed - compressed) * 100.0 / uncompressed:.1f}"


# ---------- tar ----------

class _TarError(Exception):
    pass


@dataclass
class _Entry:
    name: str
    type: str  # the tar type flag: "0" file, "1" hard link, "2" symlink, "5" directory ...
    mode: int
    uid: int
    gid: int
    uname: str
    gname: str
    size: int
    mtime: float
    link: str
    data: bytes

    @property
    def is_file(self) -> bool:
        return self.type in ("0", "\0", "7")


def _num(field: bytes) -> int:
    if field and field[0] & 0x80:  # base-256, for sizes past 8 GB
        return int.from_bytes(bytes([field[0] & 0x7F]) + field[1:], "big")
    text = field.strip(b"\0 ")
    return int(text, 8) if text else 0


def _str(field: bytes) -> str:
    return bytetext.decode(field.split(b"\0", 1)[0])


def _pax(data: bytes) -> dict[str, str]:
    out: dict[str, str] = {}
    i = 0
    while i < len(data):
        sp = data.find(b" ", i)
        if sp < 0:
            break
        length = int(data[i:sp])
        key, _, value = data[sp + 1:i + length - 1].partition(b"=")
        out[key.decode("utf-8", "replace")] = bytetext.decode(value)
        i += length
    return out


def _read_tar(data: bytes):
    """Yields the entries of a tar archive (ustar, GNU and pax headers); raises _TarError for a bad one."""
    pos, long_name, long_link, pax = 0, None, None, {}
    first = True
    while pos < len(data):
        block = data[pos:pos + 512]
        if len(block) < 512:
            raise _TarError(spec_message("tar", "notTar") if first else spec_message("tar", "unexpectedEof"))
        if block.count(0) == 512:
            return
        try:
            chk = _num(block[148:156])
            sums = sum(block[:148]) + 32 * 8 + sum(block[156:])
            signed = sum(b - 256 if b > 127 else b for b in block[:148]) + 32 * 8 + sum(b - 256 if b > 127 else b for b in block[156:])
            if chk not in (sums, signed):
                raise ValueError
            size = _num(block[124:136])
            mode, uid, gid, mtime = _num(block[100:108]), _num(block[108:116]), _num(block[116:124]), _num(block[136:148])
        except ValueError:
            raise _TarError(spec_message("tar", "notTar") if first else spec_message("tar", "unexpectedEof")) from None
        first = False
        kind = block[156:157].decode("latin-1") or "\0"
        name, link = _str(block[0:100]), _str(block[157:257])
        if block[257:262] == b"ustar" and block[262:263] == b"\0" and block[345:500].strip(b"\0"):
            name = _str(block[345:500]) + "/" + name
        end = pos + 512 + size
        if end > len(data):
            raise _TarError(spec_message("tar", "unexpectedEof"))
        body = data[pos + 512:end]
        pos = pos + 512 + (size + 511) // 512 * 512
        if kind == "L":
            long_name = _str(body)
            continue
        if kind == "K":
            long_link = _str(body)
            continue
        if kind in ("x", "g"):
            if kind == "x":
                pax = _pax(body)
            continue  # a pax global header (git archive) is metadata only
        if long_name is not None:
            name, long_name = long_name, None
        if long_link is not None:
            link, long_link = long_link, None
        if "path" in pax:
            name = pax["path"]
        if "linkpath" in pax:
            link = pax["linkpath"]
        if "mtime" in pax:
            mtime = float(pax["mtime"])
        pax = {}
        yield _Entry(name, kind, mode & 0o7777, uid, gid, _str(block[265:297]), _str(block[297:329]), len(body), mtime, link, body)


def _octal(v: int, width: int) -> bytes:
    return f"{v:0{width - 1}o}".encode()[-(width - 1):] + b"\0"


def _tar_header(name: bytes, kind: bytes, mode: int, uid: int, gid: int, size: int, mtime: int, uname: str, gname: str, link: bytes = b"") -> bytes:
    h = bytearray(512)
    h[0:len(name[:100])] = name[:100]
    h[157:157 + len(link[:100])] = link[:100]  # shortcut: a link name past 100 bytes is cut, no GNU long-link entry
    h[100:108], h[108:116], h[116:124] = _octal(mode, 8), _octal(uid, 8), _octal(gid, 8)
    h[124:136], h[136:148] = _octal(size, 12), _octal(mtime, 12)
    h[148:156] = b" " * 8
    h[156:157] = kind
    h[257:265] = b"ustar  \0"  # GNU format
    h[265:265 + len(uname.encode()[:31])] = uname.encode()[:31]
    h[297:297 + len(gname.encode()[:31])] = gname.encode()[:31]
    h[148:156] = f"{sum(h):06o}".encode() + b"\0 "
    return bytes(h)


def _pad(data: bytes) -> bytes:
    return data + b"\0" * (-len(data) % 512)


def _tar_prefix(name: str) -> str:
    """The leading part GNU tar strips from a member name: leading slashes, or everything through the last `..`."""
    cut = len(name) - len(name.lstrip("/"))
    i = 0
    while i < len(name):
        j = name.find("/", i)
        if j < 0:
            j = len(name)
        if name[i:j] == "..":
            cut = j
            while cut < len(name) and name[cut] == "/":
                cut += 1
        i = j + 1
    return name[:cut]


_USAGE = "\n" + COMMANDS["tar"].try_help.rstrip("\n")


def _tar(ctx):
    args = list(ctx.args)
    if args and args[0] and args[0][0] != "-":
        # old style: `tar czf out.tgz dir`, where f and C take the words after the bundle, in order
        bundle, rest = args[0], args[1:]
        args = []
        for c in bundle:
            args.append("-" + c)
            if c in "fC":
                if not rest:
                    raise ShellError(spec_message("tar", "oldOptionNeedsArgument", option=c) + _USAGE, 2)
                args.append(rest.pop(0))
        args += rest
    a = Args.parse(args, COMMANDS["tar"])
    modes = sum(a.has(c) for c in "cxt")
    if modes == 0:
        raise ShellError(spec_message("tar", "noMode") + _USAGE, 2)
    if modes > 1:
        raise ShellError(spec_message("tar", "manyModes") + _USAGE, 2)
    try:
        strip = int(a.value("\x01") or 0)
    except ValueError:
        raise ShellError(f"{a.value(chr(1))}: invalid number", 2) from None
    archive = a.value("f") or "-"
    fs = ctx.fs
    directory = ctx.resolve(a.value("C") or ".")

    def fatal(msg: str) -> str:
        return msg + "\ntar: " + spec_message("tar", "unrecoverable")

    if not fs.is_dir(directory):
        raise ShellError(fatal(spec_message("tar", "cannotOpen", file=a.value('C'), errno='Not a directory' if fs.exists(directory) else _why_missing(fs, directory))), 2)
    code = 0

    def fail(msg: str) -> None:
        nonlocal code
        ctx.stderr.write("tar: " + msg + "\n")
        code = 2

    def done() -> int:
        if code:
            ctx.stderr.write("tar: " + spec_message("tar", "previousErrors") + "\n")
        return code

    if a.has("c"):
        if not a.positional:
            raise ShellError(spec_message("tar", "emptyArchive") + _USAGE, 2)
        archive_path = None if archive == "-" else ctx.resolve(archive)
        verbose = ctx.stderr if archive == "-" else ctx.stdout
        out = io.BytesIO()
        stripped = False
        linked: list[tuple[str, str]] = []  # (path, member) of files with several names already stored

        def add(name: str, path: str) -> None:
            if path == archive_path:
                return  # the archive itself, as GNU tar skips it
            member = name.lstrip("/")
            try:
                st = fs.stat(path, follow=False)  # a symbolic link is stored as one, never followed
                if st.is_dir:
                    member = member.rstrip("/") + "/"
                # a file with several names: the first goes in whole, later ones as links to it
                first = next((m for p, m in linked if fs.same_file(p, path, follow=False)), None) if st.links > 1 and not st.is_dir else None
                kind = b"5" if st.is_dir else b"1" if first is not None else b"2" if st.is_symlink else b"0"
                children = fs.listdir(path) if st.is_dir else []
                body = fs.read_bytes(path) if kind == b"0" else b""
                if st.links > 1 and first is None and not st.is_dir:
                    linked.append((path, member))
                if member != "/":
                    raw = member.encode("utf-8", "surrogateescape")
                    if len(raw) > 100:  # GNU long name: a pseudo-entry carrying the name
                        out.write(_tar_header(b"././@LongLink", b"L", 0, 0, 0, len(raw) + 1, 0, "root", "root"))
                        out.write(_pad(raw + b"\0"))
                    mtime = max(0, int(st.modified.replace(tzinfo=_UTC).timestamp()))
                    uid = 0 if st.owner == "root" else 1000
                    link = (first if first is not None else st.link_target or "").encode("utf-8", "surrogateescape")
                    out.write(_tar_header(raw, kind, st.mode & 0o7777, uid, uid, len(body), mtime, st.owner, st.owner, link))
                    out.write(_pad(body))
                    if a.has("v"):
                        verbose.write(member + "\n")
                for child in children:
                    add(name.rstrip("/") + "/" + child, path.rstrip("/") + "/" + child)
            except ShellError as e:
                fail(spec_message("tar", "cannotOpen", file=name, errno=_reason(e)))

        for f in a.positional:
            if f.startswith("/") and not stripped:
                stripped = True
                ctx.stderr.write(spec_message("tar", "removingLeading", prefix="/") + "\n")
            path = normalize(directory, f)
            if not fs.exists(path, follow=False):
                fail(spec_message("tar", "cannotStat", file=f, errno=_why_missing(fs, path)))
            else:
                add(f, path)
        out.write(b"\0" * 1024)
        raw = out.getvalue()
        raw += b"\0" * (-len(raw) % 10240)  # GNU tar's default blocking factor: 20 records
        blob = _gzip(raw) if a.has("z") else raw
        if archive_path is None:
            _write(ctx, blob)
        else:
            fs.write_bytes(archive_path, blob)
        return done()

    if archive == "-":
        data = bytetext.encode(ctx.stdin)
    else:
        path = ctx.resolve(archive)
        if not fs.is_file(path):
            raise ShellError(fatal(spec_message("tar", "cannotOpen", file=archive, errno='Is a directory' if fs.is_dir(path) else _why_missing(fs, path))), 2)
        data = fs.read_bytes(path)
    if _is_gzip(data):
        if not a.has("z") and archive == "-":
            raise ShellError(fatal(spec_message("tar", "compressedArchive")), 2)
        plain, error = _gunzip(data, _Budget(ctx.shell, 2))
        if plain is None:
            ctx.stderr.write(spec_message("tar", "gzipChildFailed", error=error) + "\n")
            raise ShellError(spec_message("tar", "unrecoverable"), 2)
        data = plain
    elif a.has("z"):
        ctx.stderr.write(spec_message("tar", "gzipChildFailed", error="not in gzip format") + "\n")
        raise ShellError(spec_message("tar", "unrecoverable"), 2)

    budget = _Budget(ctx.shell, 2)
    made: set[str] = set()  # symbolic links this extraction created

    def through_made_link(path: str) -> bool:
        p = get_parent(path)
        while len(p) > len(directory) and p != "/":
            if p in made and fs.is_symlink(p):
                return True
            p = get_parent(p)
        return False

    wanted = [p.rstrip("/") for p in a.positional]
    found = [False] * len(wanted)
    announced: set[str] = set()
    info = ctx.stderr if a.has("O") else ctx.stdout
    ug_width = 19
    entries = _read_tar(data)
    while True:
        try:
            e = next(entries, None)
        except _TarError as ex:
            fail(str(ex))
            break
        if e is None:
            break
        budget.entry()
        name = e.name
        prefix = _tar_prefix(name)
        rel = name[len(prefix):]
        if wanted:
            hit = next((i for i, w in enumerate(wanted) if rel.rstrip("/") == w or rel.startswith(w + "/")), -1)
            if hit < 0:
                continue
            found[hit] = True
        if prefix and prefix not in announced:
            announced.add(prefix)
            ctx.stderr.write(spec_message("tar", "removingLeading", prefix=prefix) + "\n")
        link_prefix = _tar_prefix(e.link) if e.type == "1" else ""
        if link_prefix and "link:" + link_prefix not in announced:
            announced.add("link:" + link_prefix)
            ctx.stderr.write(spec_message("tar", "removingLeadingLink", prefix=link_prefix) + "\n")
        if a.has("t"):
            if not a.has("v"):
                ctx.print(name)
                continue
            kind = {"5": "d", "2": "l", "1": "h", "3": "c", "4": "b", "6": "p"}.get(e.type, "-")
            ug = f"{e.uname or e.uid}/{e.gname or e.gid}"
            size = str(e.size if e.is_file else 0)
            ug_width = max(ug_width, len(ug) + 1 + len(size))  # grows like GNU tar's, so later columns stay aligned
            link = " -> " + e.link if e.type == "2" else " link to " + e.link[len(link_prefix):] if e.type == "1" else ""
            ctx.print(f"{kind}{format_mode(False, e.mode)[1:]} {ug}{size.rjust(ug_width - len(ug))} "
                      f"{_utc_naive(e.mtime):%Y-%m-%d %H:%M} {name}{link}")
            continue
        if ".." in prefix.split("/"):
            fail(spec_message("tar", "memberDotDot", name=name))
            continue
        parts = [p for p in rel.split("/") if p]
        if len(parts) <= strip:
            continue
        target = normalize(directory, "/".join(parts[strip:]))
        try:
            if a.has("O"):
                if not e.is_file:
                    continue
                if a.has("v"):
                    info.write(name + "\n")
                budget.charge(len(e.data))
                _write(ctx, e.data)
                continue
            if a.has("v"):
                info.write(name + "\n")
            # like GNU tar's placeholders for links: nothing lands under (or links to something under) a symbolic link this
            # archive made, so a crafted member can't use one to write outside the target directory
            if through_made_link(target):
                raise ShellError(f"{target}: Not a directory")
            if e.type != "5" and fs.exists(target, follow=False) and not fs.is_dir(target, follow=False):
                fs.delete(target)  # a member replaces what is there, never writes through a link
            if e.type == "5":
                fs.mkdir(target, parents=True)  # shortcut: the archived directory mode isn't restored, so read-only dirs can't block their own contents
            elif e.is_file:
                budget.charge(len(e.data))
                fs.mkdir(get_parent(target), parents=True)
                fs.write_bytes(target, e.data)
                fs.chmod(target, e.mode & 0o777)
                fs.set_modified_time(target, _utc_naive(e.mtime))
            elif e.type == "1":
                link_parts = [p for p in e.link[len(link_prefix):].split("/") if p]
                link_target = normalize(directory, "/".join(link_parts[strip:]))
                # only to a file this archive (or the target dir) already holds, never through a link it made
                if (len(link_parts) <= strip or not fs.exists(link_target, follow=False) or fs.is_dir(link_target, follow=False)
                        or link_target == target or through_made_link(link_target)):
                    fail(f"{name}: Cannot hard link to ‘{'/'.join(link_parts)}’: No such file or directory")
                    continue
                fs.mkdir(get_parent(target), parents=True)
                fs.create_hard_link(link_target, target)
            elif e.type == "2":
                fs.mkdir(get_parent(target), parents=True)
                fs.create_symlink(target, e.link)
                made.add(target)
                fs.set_modified_time(target, _utc_naive(e.mtime), follow=False)
            else:
                fail(spec_message("tar", "cannotMknod", name=name))
        except ShellError as ex:
            if "extraction limit" in str(ex):
                raise
            fail(spec_message("tar", "cannotOpen", file=name, errno=_reason(ex)))
    for i, w in enumerate(wanted):
        if not found[i]:
            fail(spec_message("tar", "notFound", file=a.positional[i]))
    return done()


# ---------- gzip command ----------

def _gzip_cmd(ctx):
    a = Args.parse(ctx.args, COMMANDS[ctx.name])
    decompress = ctx.name != "gzip" or a.has("d") or a.has("t") or a.has("l")
    to_stdout = ctx.name == "zcat" or a.has("c")
    level = max((int(c) for c in "123456789" if a.has(c)), default=6)
    fs = ctx.fs
    budget = _Budget(ctx.shell, 1)
    code = 0

    def error(msg: str) -> None:
        nonlocal code
        ctx.stderr.write("gzip: " + msg + "\n")
        code = 1

    def warn(msg: str) -> None:
        nonlocal code
        if not a.has("q"):
            ctx.stderr.write("gzip: " + msg + "\n")
        if code == 0:
            code = 2

    def replace(src: str, dst: str, blob: bytes) -> None:
        """Writes the result next to the source with its mode and time, and removes the source unless -k."""
        st = fs.stat(src)
        fs.write_bytes(dst, blob)
        fs.chmod(dst, st.mode)
        fs.set_modified_time(dst, st.modified)
        if not a.has("k"):
            fs.delete(src)

    files = a.positional or ["-"]
    total_c = total_u = total_h = 0
    header = False
    for f in files:
        path = ctx.resolve(f)
        if decompress and f != "-" and not fs.exists(path) and not f.endswith(".gz"):
            f, path = f + ".gz", path + ".gz"
        if f == "-":
            data = bytetext.encode(ctx.stdin)
        elif fs.is_dir(path):
            warn(spec_message("gzip", "isDirectory", file=f))
            continue
        elif not fs.is_file(path):
            error(spec_message("gzip", "fileError", file=f, errno=_why_missing(fs, path)))
            continue
        else:
            data = fs.read_bytes(path)
        shown = "stdin" if f == "-" else f

        if not decompress:
            packed = _gzip(data, level)
            if f == "-" or to_stdout:
                _write(ctx, packed)
                continue
            if f.endswith(".gz"):
                if not a.has("q"):
                    ctx.stderr.write("gzip: " + spec_message("gzip", "alreadySuffix", file=f) + "\n")
                continue
            if fs.exists(path + ".gz") and not a.has("f"):
                warn(spec_message("gzip", "alreadyExists", file=f + ".gz"))
                continue
            replace(path, path + ".gz", packed)
            if a.has("v"):
                ctx.stderr.write(spec_message("gzip", "ratio", file=f, ratio=_ratio(len(packed) - _gzip_overhead(packed), len(data)), action='created' if a.has('k') else 'replaced with', target=f + ".gz") + "\n")
            continue

        out_name = None
        if f != "-" and not to_stdout and not a.has("t") and not a.has("l"):
            suffix = next((x for x in (".gz", "-gz", ".z", "_z", ".tgz") if f.endswith(x) and len(f) > len(x)), None)
            if suffix is None:
                warn(spec_message("gzip", "unknownSuffix", file=f))
                continue
            out_name = f[:-len(suffix)] + (".tar" if suffix == ".tgz" else "")
        if not _is_gzip(data):
            if a.has("f") and to_stdout:  # gzip -dcf passes plain data through
                _write(ctx, data)
                continue
            ctx.stderr.write(spec_message("gzip", "notGzip", file=shown) + "\n")
            code = 1
            continue
        if a.has("l"):
            c, u, h = len(data), struct.unpack("<I", data[-4:])[0] if len(data) >= 4 else 0, _gzip_overhead(data)
            total_c, total_u, total_h = total_c + c, total_u + u, total_h + h
            name = "stdout" if f == "-" else f[:-3] if f.endswith(".gz") else f
            if not header:
                header = True
                ctx.print("         compressed        uncompressed  ratio uncompressed_name")
            ctx.print(f"{c:>19} {u:>19} {_ratio(c - h, u):>5}% {name}")
            continue
        plain, err = _gunzip(data, budget)
        if plain is None:
            ctx.stderr.write(spec_message("gzip", "corrupt", file=shown, error=err) + "\n")
            code = 1
            continue
        if a.has("t"):
            continue
        if out_name is None:
            _write(ctx, plain)
            continue
        out_path = ctx.resolve(out_name)
        if fs.exists(out_path) and not a.has("f"):
            warn(spec_message("gzip", "alreadyExists", file=out_name))
            continue
        replace(path, out_path, plain)
        if a.has("v"):
            ctx.stderr.write(spec_message("gzip", "ratio", file=f, ratio=_ratio(len(data) - _gzip_overhead(data), len(plain)), action='replaced with', target=out_name) + "\n")
    if header and len(files) > 1:
        ctx.print(f"{total_c:>19} {total_u:>19} {_ratio(total_c - total_h, total_u):>5}% (totals)")
    return code


# ---------- zip ----------

def _deflated_length(data: bytes) -> int:
    z = zlib.compressobj(6, zlib.DEFLATED, -15)
    return len(z.compress(data) + z.flush())


def _zip(ctx):
    a = Args.parse(ctx.args, COMMANDS["zip"])
    fs = ctx.fs
    if not a.positional:
        raise ShellError(spec_message("zip", "noArchive"), 12)
    archive = a.positional[0]
    if archive != "-" and "." not in archive.rstrip("/").rsplit("/", 1)[-1]:
        archive += ".zip"
    archive_path = None if archive == "-" else ctx.resolve(archive)
    info = ctx.stderr if archive_path is None else ctx.stdout
    entries: dict[str, tuple[zipfile.ZipInfo, bytes]] = {}  # name -> (header, uncompressed data), in archive order
    if archive_path is not None and fs.is_file(archive_path):
        try:
            with zipfile.ZipFile(io.BytesIO(fs.read_bytes(archive_path))) as zf:
                for zi in zf.infolist():
                    entries[zi.filename] = (zi, zf.read(zi))
        except (zipfile.BadZipFile, NotImplementedError, RuntimeError, zlib.error):
            raise ShellError(spec_message("zip", "badStructure", archive=archive), 3) from None
    added = 0
    walking: set[str] = set()  # real paths of the directories being added

    def add(name: str, path: str) -> None:
        nonlocal added
        if path == archive_path or not fs.exists(path):  # a dangling link has nothing to store
            return
        ctx.check_time()  # following links, -r can walk far more than the tree: the call's time limit
        st = fs.stat(path)
        entry_name = "/".join(p for p in name.split("/") if p not in ("", ".")) + ("/" if st.is_dir else "")
        if entry_name != "/":
            data = b"" if st.is_dir else fs.read_bytes(path)
            deflated = _deflated_length(data) if data else 0
            stored = deflated == 0 or deflated >= len(data)
            old = entries.pop(entry_name, None)
            when = max(st.modified, datetime(1980, 1, 1))
            zi = zipfile.ZipInfo(entry_name, (when.year, when.month, when.day, when.hour, when.minute, when.second))
            zi.compress_type = zipfile.ZIP_STORED if stored else zipfile.ZIP_DEFLATED
            zi.create_system = 3
            zi.external_attr = ((0x4000 if st.is_dir else 0x8000) | (st.mode & 0o7777)) << 16 | (0x10 if st.is_dir else 0)
            entries[entry_name] = (zi, data)
            added += 1
            if not a.has("q"):
                how = "stored 0" if stored else f"deflated {(200 * (len(data) - deflated) // len(data) + 1) >> 1}"
                info.write(f"{'updating' if old else '  adding'}: {entry_name} ({how}%)\n")
        if st.is_dir and (a.has("r") or entry_name == "/"):
            # zip follows links to directories: one leading back up the tree would never end
            real = fs.real_path(path)[0]
            if real is None or real in walking:
                return
            walking.add(real)
            for child in fs.listdir(path):
                add(name.rstrip("/") + "/" + child, path.rstrip("/") + "/" + child)
            walking.discard(real)

    names = a.positional[1:]
    for f in names:
        if not fs.exists(ctx.resolve(f)) and not a.has("q"):
            info.write(spec_message("zip", "nameNotMatched", file=f) + "\n")
    for f in names:
        if fs.exists(ctx.resolve(f)):
            add(f, ctx.resolve(f))
    if added == 0:
        hint = f"try: zip -r {archive} . -i {' '.join(names)}" if a.has("r") else archive
        info.write(spec_message("zip", "nothingToDo", hint=hint) + "\n")
        return 12
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        for zi, data in entries.values():
            zf.writestr(zi, data, compress_type=zi.compress_type)
    if archive_path is None:
        _write(ctx, buf.getvalue())
    else:
        fs.write_bytes(archive_path, buf.getvalue())
    return 0


def _unzip(ctx):
    a = Args.parse(ctx.args, COMMANDS["unzip"])
    fs = ctx.fs
    if not a.positional:
        ctx.print("UnZip 6.00 of 20 April 2009, by Debian. Original by Info-ZIP.\n\n"
                  "Usage: unzip [-Z] [-opts[modifiers]] file[.zip] [list] [-x xlist] [-d exdir]")
        return 0
    name = a.positional[0]
    path = next((p for p in map(ctx.resolve, (name, name + ".zip", name + ".ZIP")) if fs.is_file(p)), None)
    if path is None:
        ctx.stderr.write(spec_message("unzip", "cannotFind", name=name) + "\n")
        return 9
    quiet, pipe = a.has("q"), a.has("p")
    try:
        zf = zipfile.ZipFile(io.BytesIO(fs.read_bytes(path)))
    except zipfile.BadZipFile:
        if not quiet:
            ctx.print(f"Archive:  {name}")
        ctx.stderr.write(spec_message("unzip", "noEndSignature") + "\n")
        ctx.stderr.write(spec_message("unzip", "noDirectory", name=name) + "\n")
        return 9
    if not quiet and not pipe:
        ctx.print(f"Archive:  {name}")
    patterns = [glob_to_regex(p, slashes=True) for p in a.positional[1:]]
    found = [False] * len(patterns)

    def selected(zi: zipfile.ZipInfo) -> bool:
        if not patterns:
            return True
        hit = False
        for i, p in enumerate(patterns):
            if p.match(zi.filename):
                hit = found[i] = True
        return hit

    budget = _Budget(ctx.shell, 1)
    code = 0

    def inflate(zi: zipfile.ZipInfo) -> bytes:
        try:
            with zf.open(zi) as s:
                return budget.read(s)
        except (zipfile.BadZipFile, NotImplementedError, RuntimeError, zlib.error) as ex:
            raise ShellError(f"{zi.filename}: {ex}", 2) from None

    with zf:
        if a.has("l"):
            ctx.print("  Length      Date    Time    Name\n---------  ---------- -----   ----")
            total = count = 0
            for zi in filter(selected, zf.infolist()):
                budget.entry()
                total += zi.file_size
                count += 1
                y, mo, d, h, mi, _ = zi.date_time
                ctx.print(f"{zi.file_size:>9}  {y:04}-{mo:02}-{d:02} {h:02}:{mi:02}   {zi.filename}")
            ctx.print(f"---------                     -------\n{total:>9}                     {count} file{'' if count == 1 else 's'}")
        else:
            dest = ctx.resolve(a.value("d") or ".")
            shown_dir = a.value("d").rstrip("/") + "/" if a.value("d") else ""
            none = False
            for zi in filter(selected, zf.infolist()):
                budget.entry()
                entry_name = zi.filename
                is_dir = entry_name.endswith("/")
                if pipe:
                    if not is_dir:
                        _write(ctx, inflate(zi))
                    continue
                parts = entry_name.split("/")
                if ".." in parts:
                    if not quiet:
                        ctx.print(spec_message("unzip", "skippedDotDot", name=entry_name))
                    code = 1
                if entry_name.startswith("/"):
                    ctx.stderr.write(spec_message("unzip", "strippedAbsolute", name=entry_name) + "\n")
                    code = 1
                rel = "/".join(p for p in parts if p not in ("", ".", ".."))
                if not rel:
                    continue
                target = normalize(dest, rel)
                shown = shown_dir + rel + ("/" if is_dir else "")
                try:
                    if is_dir:
                        if fs.is_dir(target):
                            continue
                        fs.mkdir(target, parents=True)
                        if not quiet:
                            ctx.print(f"   creating: {shown}")
                        continue
                    if fs.exists(target) and not a.has("o"):
                        if a.has("n") or none:
                            continue
                        # shortcut: no interactive stdin, so the prompt always reads EOF, as unzip does with </dev/null
                        ctx.stderr.write(spec_message("unzip", "replacePrompt", file=shown) + "\n")
                        none = True
                        code = max(code, 1)
                        continue
                    data = inflate(zi)
                    fs.mkdir(get_parent(target), parents=True)
                    fs.write_bytes(target, data)
                    mode = zi.external_attr >> 16 & 0o777
                    if mode:
                        fs.chmod(target, mode)
                    fs.set_modified_time(target, datetime(*zi.date_time))
                    if not quiet:
                        ctx.print(f"{' extracting' if zi.compress_size == zi.file_size else '  inflating'}: {shown:<22}  ")
                except ShellError as ex:
                    if "extraction limit" in str(ex):
                        raise
                    ctx.stderr.write(spec_message("unzip", "cannotCreate", file=shown, errno=_reason(ex)) + "\n")
                    code = max(code, 1)
    for i, p in enumerate(found):
        if not p:
            if not a.has("l"):
                ctx.stderr.write(spec_message("unzip", "filenameNotMatched", pattern=a.positional[i + 1]) + "\n")
            code = 11
    return code


def register(shell) -> None:
    r = shell.register_command
    r("tar", _tar)
    r("gzip", _gzip_cmd)
    r("gunzip", _gzip_cmd)
    r("zcat", _gzip_cmd)
    r("zip", _zip)
    r("unzip", _unzip)
