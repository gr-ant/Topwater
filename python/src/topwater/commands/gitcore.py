"""git's on-disk formats and algorithms for the `git` command: loose objects, the v2 index, refs, config, ignore rules,
revisions, line diffs and three-way merges. Written from the publicly documented formats; reads and writes repositories
real git can use. Everything read from .git is untrusted: sizes, depths and walks are bounded, and no path is followed
through a symbolic link."""
from __future__ import annotations

import hashlib
import re
import struct
import zlib
from datetime import datetime, timedelta, timezone

from ..vfs import utc_now
from . import spec_message

MAX_WALK = 1_000_000  # commits one history walk may visit
MAX_DEPTH = 256  # nested trees, and directories in one path
DIFF_BUDGET = 1_000_000  # Myers steps per file pair before falling back to "all removed, all added"
EMPTY_TREE = "4b825dc642cb6eb9a060e54bf8d69288fbee4904"
HEX = re.compile(r"[0-9a-f]{4,40}")


class GitError(Exception):
    """Ends the git command: message (whole lines, already worded) on stderr and the exit code."""

    def __init__(self, message: str, code: int = 128):
        super().__init__(message)
        self.code = code


def msg(key: str, /, **values) -> str:
    return spec_message("git", key, **values)


def fatal(key: str, /, code: int = 128, **values) -> GitError:
    return GitError(msg(key, **values), code)


def sha1(data: bytes) -> str:
    return hashlib.sha1(data).hexdigest()


def object_id(kind: str, data: bytes) -> str:
    return sha1(f"{kind} {len(data)}\0".encode() + data)


# ---------- identity and dates ----------

def parse_date(text: str, now: datetime) -> tuple[int, str]:
    """GIT_AUTHOR_DATE / --date: '@1700000000 +0100', '1700000000 +0100', ISO 8601 or RFC 2822 -> (epoch, '+hhmm')."""
    s = text.strip()
    m = re.fullmatch(r"@?(\d+)(?:\s+([+-]\d{4}))?", s)
    if m:
        return int(m.group(1)), m.group(2) or "+0000"
    m = re.fullmatch(r"(?:\w{3},\s*)?(\d{1,2})\s+(\w{3})\s+(\d{4})\s+(\d{1,2}):(\d{2})(?::(\d{2}))?\s*([+-]\d{4})?", s)
    if m:
        months = "jan feb mar apr may jun jul aug sep oct nov dec".split()
        if m.group(2).lower() not in months:
            raise ValueError(text)
        d = datetime(int(m.group(3)), months.index(m.group(2).lower()) + 1, int(m.group(1)), int(m.group(4)), int(m.group(5)), int(m.group(6) or 0))
        return _with_zone(d, m.group(7) or "+0000")
    m = re.fullmatch(r"(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{1,2}):(\d{2})(?::(\d{2}))?(?:\.\d+)?)?\s*(Z|[+-]\d{2}:?\d{2})?", s)
    if m:
        d = datetime(int(m.group(1)), int(m.group(2)), int(m.group(3)), int(m.group(4) or 0), int(m.group(5) or 0), int(m.group(6) or 0))
        z = m.group(7) or "+0000"
        return _with_zone(d, "+0000" if z == "Z" else z.replace(":", ""))
    if s == "now":
        return int(now.replace(tzinfo=timezone.utc).timestamp()), "+0000"
    raise ValueError(text)


def _with_zone(local: datetime, zone: str) -> tuple[int, str]:
    off = (int(zone[1:3]) * 60 + int(zone[3:5])) * (-1 if zone[0] == "-" else 1)
    return int((local - timedelta(minutes=off)).replace(tzinfo=timezone.utc).timestamp()), zone


def zone_delta(zone: str) -> timedelta:
    try:
        return timedelta(minutes=(int(zone[1:3]) * 60 + int(zone[3:5])) * (-1 if zone[0] == "-" else 1))
    except (ValueError, IndexError):
        return timedelta(0)


def local_time(epoch: int, zone: str) -> datetime:
    return datetime(1970, 1, 1) + timedelta(seconds=epoch) + zone_delta(zone)


def format_date(epoch: int, zone: str, style: str = "default", now: datetime | None = None) -> str:
    t = local_time(epoch, zone)
    if style in ("default", ""):
        return f"{t:%a %b} {t.day} {t:%H:%M:%S %Y} {zone}"
    if style == "short":
        return f"{t:%Y-%m-%d}"
    if style == "iso" or style == "iso8601":
        return f"{t:%Y-%m-%d %H:%M:%S} {zone}"
    if style in ("iso-strict", "iso8601-strict"):
        return f"{t:%Y-%m-%dT%H:%M:%S}{zone[:3]}:{zone[3:]}"
    if style == "rfc" or style == "rfc2822":
        return f"{t:%a}, {t.day} {t:%b %Y %H:%M:%S} {zone}"
    if style == "raw":
        return f"{epoch} {zone}"
    if style == "unix":
        return str(epoch)
    if style == "relative":
        return relative_date(epoch, now or utc_now())
    raise GitError(msg("badDateFormat", format=style))


def relative_date(epoch: int, now: datetime) -> str:
    diff = int(now.replace(tzinfo=timezone.utc).timestamp()) - epoch
    if diff < 0:
        return "in the future"
    for limit, unit, size in ((90, "second", 1), (90 * 60, "minute", 60), (36 * 3600, "hour", 3600), (14 * 86400, "day", 86400),
                              (70 * 86400, "week", 7 * 86400), (365 * 86400, "month", 30 * 86400)):
        if diff < limit:
            n = (diff + size // 2) // size
            return f"{n} {unit}{'s' if n != 1 else ''} ago"
    years = diff // (365 * 86400)
    return f"{years} year{'s' if years != 1 else ''} ago"


# ---------- objects ----------

class Commit:
    __slots__ = ("sha", "tree", "parents", "author", "committer", "message")

    def __init__(self, sha, tree, parents, author, committer, message):
        self.sha, self.tree, self.parents, self.author, self.committer, self.message = sha, tree, parents, author, committer, message

    @property
    def subject(self) -> str:
        paras = self.message.lstrip("\n").split("\n\n", 1)
        return " ".join(paras[0].split("\n")).strip() if paras else ""

    @property
    def body(self) -> str:
        parts = self.message.lstrip("\n").split("\n\n", 1)
        return parts[1].lstrip("\n") if len(parts) > 1 else ""


def parse_ident(line: str) -> tuple[str, str, int, str]:
    """'Name <email> 1700000000 +0000' -> (name, email, epoch, zone)."""
    m = re.fullmatch(r"(.*?) ?<([^<>]*)> ?(\d+)? ?([+-]\d{4})?", line)
    if not m:
        return line, "", 0, "+0000"
    return m.group(1), m.group(2), int(m.group(3) or 0), m.group(4) or "+0000"


def parse_tree(data: bytes, where: str) -> list[tuple[str, str, str]]:
    """Tree object -> [(mode, name, sha)]; names are checked so nothing can point outside the work tree."""
    out, i = [], 0
    while i < len(data):
        sp = data.find(b" ", i)
        nul = data.find(b"\0", sp + 1) if sp >= 0 else -1
        if sp < 0 or nul < 0 or nul + 21 > len(data):
            raise GitError(msg("corruptObject", object=where))
        mode = data[i:sp].decode("ascii", "replace")
        name = data[sp + 1:nul].decode("utf-8", "surrogateescape")
        if not re.fullmatch(r"[0-7]{5,6}", mode):
            raise GitError(msg("corruptObject", object=where))
        check_component(name)
        out.append((mode.zfill(6) if mode == "40000" else mode, name, data[nul + 1:nul + 21].hex()))
        i = nul + 21
    return out


def check_component(name: str) -> None:
    if not name or name in (".", "..") or "/" in name or "\0" in name or name.lower() == ".git":
        raise GitError(msg("invalidPath", path=name))


def check_path(path: str) -> None:
    parts = path.split("/")
    if len(parts) > MAX_DEPTH:
        raise GitError(msg("pathTooDeep", path=path[:200] + "...", limit=str(MAX_DEPTH)))
    for part in parts:
        check_component(part)


def tree_bytes(entries) -> bytes:
    """[(mode, name, sha)] -> tree object, in git's order (a directory sorts as if its name ended in '/')."""
    def key(e):
        n = e[1].encode("utf-8", "surrogateescape")
        return n + b"/" if e[0] == "040000" else n
    out = bytearray()
    for mode, name, sha in sorted(entries, key=key):
        out += (mode.lstrip("0") if mode == "040000" else mode).encode() + b" " + name.encode("utf-8", "surrogateescape") + b"\0" + bytes.fromhex(sha)
    return bytes(out)


# ---------- the index ----------

class Entry:
    __slots__ = ("path", "sha", "mode", "stage", "mtime", "size")

    def __init__(self, path, sha, mode, stage=0, mtime=0, size=0):
        self.path, self.sha, self.mode, self.stage, self.mtime, self.size = path, sha, mode, stage, mtime, size


def read_index(data: bytes, limit: int) -> list[Entry]:
    if len(data) < 32 or data[:4] != b"DIRC":
        raise GitError(msg("badIndex", why=msg("indexBadSignature")))
    version, count = struct.unpack(">II", data[4:12])
    if version not in (2, 3):
        raise GitError(msg("badIndex", why=msg("indexBadVersion", version=str(version))))
    if sha1(data[:-20]) != data[-20:].hex():
        raise GitError(msg("badIndex", why=msg("indexBadChecksum")))
    if count > limit:
        raise GitError(msg("badIndex", why=msg("indexTooMany")))
    out, i, end = [], 12, len(data) - 20
    for _ in range(count):
        if i + 62 > end:
            raise GitError(msg("badIndex", why=msg("indexTruncated")))
        mtime, = struct.unpack(">I", data[i + 8:i + 12])
        mode, = struct.unpack(">I", data[i + 24:i + 28])
        size, = struct.unpack(">I", data[i + 36:i + 40])
        sha = data[i + 40:i + 60].hex()
        flags, = struct.unpack(">H", data[i + 60:i + 62])
        start = i + 62
        if flags & 0x4000:
            if version < 3:
                raise GitError(msg("badIndex", why=msg("indexExtendedFlag")))
            start += 2
        nul = data.find(b"\0", start, end)
        if nul < 0:
            raise GitError(msg("badIndex", why=msg("indexTruncated")))
        path = data[start:nul].decode("utf-8", "surrogateescape")
        check_path(path)
        out.append(Entry(path, sha, f"{mode:06o}", (flags >> 12) & 3, mtime, size))
        i += ((nul - i) // 8 + 1) * 8  # entries are NUL-padded to a multiple of eight bytes
    while i + 8 <= end:  # extensions: optional ones (A-Z first) are skipped, others are not understood
        sig, size = data[i:i + 4], struct.unpack(">I", data[i + 4:i + 8])[0]
        if not 65 <= sig[0] <= 90:
            raise GitError(msg("badIndex", why=msg("indexBadExtension", name=sig.decode("latin-1"))))
        i += 8 + size
    return out


def write_index(entries: list[Entry]) -> bytes:
    out = bytearray(b"DIRC" + struct.pack(">II", 2, len(entries)))
    for e in sorted(entries, key=lambda e: (e.path.encode("utf-8", "surrogateescape"), e.stage)):
        name = e.path.encode("utf-8", "surrogateescape")
        start = len(out)
        out += struct.pack(">IIIIIIIIII", e.mtime, 0, e.mtime, 0, 0, 0, int(e.mode, 8), 0, 0, e.size & 0xFFFFFFFF)
        out += bytes.fromhex(e.sha) + struct.pack(">H", (e.stage << 12) | min(len(name), 0xFFF)) + name
        out += b"\0" * (8 - (len(out) - start) % 8)
    return bytes(out + bytes.fromhex(sha1(bytes(out))))


# ---------- config ----------

class Config:
    """git config files: [section], [section "sub"], key = value; names are matched case-insensitively (not subsections)."""

    def __init__(self, text: str = ""):
        self.lines = text.split("\n")
        if self.lines and self.lines[-1] == "":
            self.lines.pop()

    def items(self):
        """(name, value, line index) in file order; name is section[.sub].key with section and key lowercased."""
        section = None
        i = 0
        while i < len(self.lines):
            line, at = self.lines[i], i
            while line.rstrip().endswith("\\") and i + 1 < len(self.lines) and not _in_comment(line):
                i += 1
                line = line.rstrip()[:-1] + self.lines[i]
            i += 1
            s = line.strip()
            if not s or s[0] in "#;":
                continue
            if s.startswith("["):
                m = re.match(r'\[\s*([A-Za-z0-9.-]+)(?:\s+"((?:[^"\\]|\\.)*)")?\s*\]', s)
                if not m:
                    raise GitError(msg("badConfig", line=str(at + 1)))
                name = m.group(1)
                if m.group(2) is not None:
                    section = name.lower() + "." + re.sub(r"\\(.)", r"\1", m.group(2))
                elif "." in name:
                    head, _, sub = name.partition(".")
                    section = head.lower() + "." + sub
                else:
                    section = name.lower()
                s = s[m.end():].strip()
                if not s or s[0] in "#;":
                    continue
            if section is None:
                raise GitError(msg("badConfig", line=str(at + 1)))
            m = re.match(r"([A-Za-z][A-Za-z0-9-]*)\s*(=\s*(.*))?$", s)
            if not m:
                raise GitError(msg("badConfig", line=str(at + 1)))
            yield f"{section}.{m.group(1).lower()}", (_value(m.group(3)) if m.group(2) else "true"), at

    def get(self, name: str):
        val = None
        for n, v, _ in self.items():
            if n == name:
                val = v
        return val

    def set(self, name: str, value: str | None) -> bool:
        """Sets (or with None removes) name; True when something changed."""
        section, _, key = name.rpartition(".")
        last = None
        found = [at for n, _, at in self.items() if n == name]
        if value is None:
            for at in reversed(found):
                del self.lines[at]
            self._drop_empty_sections()
            return bool(found)
        line = f"\t{key} = {_quote(value)}"
        if found:
            self.lines[found[-1]] = line
            return True
        # the end of the last block for this section
        cur = None
        for i, l in enumerate(self.lines):
            s = l.strip()
            if s.startswith("["):
                cur = _section_of(s)
                if cur == section:
                    last = i
            elif cur == section and s:
                last = i
        if last is not None:
            self.lines.insert(last + 1, line)
        else:
            head, _, sub = section.partition(".")
            self.lines.append(f'[{head} "{sub}"]' if sub else f"[{head}]")
            self.lines.append(line)
        return True

    def _drop_empty_sections(self) -> None:
        """A section header left with nothing under it goes too (as git does after removing its last key)."""
        keep, i = [], 0
        while i < len(self.lines):
            s = self.lines[i].strip()
            if s.startswith("[") and s.endswith("]"):
                j = i + 1
                while j < len(self.lines) and not self.lines[j].strip():
                    j += 1
                if j >= len(self.lines) or self.lines[j].strip().startswith("["):
                    i = j
                    continue
            keep.append(self.lines[i])
            i += 1
        self.lines = keep

    def text(self) -> str:
        return "\n".join(self.lines) + "\n" if self.lines else ""


def _section_of(s: str):
    m = re.match(r'\[\s*([A-Za-z0-9.-]+)(?:\s+"((?:[^"\\]|\\.)*)")?\s*\]', s)
    if not m:
        return None
    if m.group(2) is not None:
        return m.group(1).lower() + "." + m.group(2)
    head, _, sub = m.group(1).partition(".")
    return head.lower() + ("." + sub if sub else "")


def _in_comment(line: str) -> bool:
    q = False
    for c in line:
        if c == '"':
            q = not q
        elif c in "#;" and not q:
            return True
    return False


def _value(raw: str) -> str:
    out, q, i = [], False, 0
    raw = raw.rstrip()
    while i < len(raw):
        c = raw[i]
        if c == '"':
            q = not q
        elif c == "\\" and i + 1 < len(raw):
            i += 1
            out.append({"n": "\n", "t": "\t", "b": "\b"}.get(raw[i], raw[i]))
        elif c in "#;" and not q:
            break
        else:
            out.append(c)
        i += 1
    return "".join(out).strip() if not q else "".join(out)


def _quote(v: str) -> str:
    e = v.replace("\\", "\\\\").replace('"', '\\"').replace("\n", "\\n").replace("\t", "\\t")
    return f'"{e}"' if v != v.strip() or any(c in v for c in "#;") else e


def config_bool(v) -> bool | None:
    if v is None:
        return None
    v = v.lower()
    if v in ("true", "yes", "on", "1", ""):
        return True
    if v in ("false", "no", "off", "0"):
        return False
    return None


def valid_key(name: str) -> bool:
    section, _, key = name.rpartition(".")
    return bool(section) and re.fullmatch(r"[A-Za-z][A-Za-z0-9-]*", key) is not None and re.fullmatch(r"[A-Za-z0-9-]+(\..+)?", section) is not None


def canonical_key(name: str) -> str:
    head, _, rest = name.partition(".")
    sub, _, key = rest.rpartition(".")
    return f"{head.lower()}.{sub}.{key.lower()}" if sub else f"{head.lower()}.{key.lower()}"


# ---------- ignore rules ----------

def glob_regex(pattern: str, full_path: bool) -> str:
    """A gitignore / pathspec glob as a regex: * and ? stop at '/', ** spans directories, [...] is a class."""
    out, i, n = [], 0, len(pattern)
    while i < n:
        c = pattern[i]
        if c == "*":
            if pattern.startswith("**", i) and (i == 0 or pattern[i - 1] == "/") and (i + 2 == n or pattern[i + 2] == "/"):
                if i + 2 == n:
                    out.append(".*")
                    i += 2
                else:
                    out.append("(?:.*/)?")
                    i += 3
                continue
            out.append("[^/]*" if full_path else ".*")
        elif c == "?":
            out.append("[^/]" if full_path else ".")
        elif c == "[":
            j = i + 1
            if j < n and pattern[j] in "!^":
                j += 1
            if j < n and pattern[j] == "]":
                j += 1
            while j < n and pattern[j] != "]":
                j += 1
            if j >= n:
                out.append("\\[")
            else:
                body = pattern[i + 1:j]
                if body[:1] in ("!", "^"):
                    body = "^" + body[1:]
                out.append("[" + body.replace("\\", "\\\\") + "]")
                i = j
        elif c == "\\" and i + 1 < n:
            i += 1
            out.append(re.escape(pattern[i]))
        else:
            out.append(re.escape(c))
        i += 1
    return "".join(out)


class IgnoreRules:
    """One ignore file's patterns, relative to its directory (base, '' for the top)."""

    def __init__(self, text: str, base: str):
        self.base = base
        self.rules = []
        for line in text.split("\n")[:10_000]:
            line = line.rstrip("\r")
            if not line or line.startswith("#"):
                continue
            while line.endswith(" ") and not line.endswith("\\ "):
                line = line[:-1]
            neg = line.startswith("!")
            if neg:
                line = line[1:]
            elif line.startswith("\\!") or line.startswith("\\#"):
                line = line[1:]
            if not line:
                continue
            dir_only = line.endswith("/")
            line = line.rstrip("/")
            anchored = "/" in line
            line = line.lstrip("/")
            try:
                rx = timed(glob_regex(line, True) + r"\Z", re.S)
            except re.error:
                continue
            self.rules.append((rx, neg, dir_only, anchored))

    def match(self, path: str, is_dir: bool):
        """None when no pattern speaks about path (relative to the top), else whether it is ignored."""
        rel = path[len(self.base) + 1:] if self.base else path
        name = rel.rsplit("/", 1)[-1]
        for rx, neg, dir_only, anchored in reversed(self.rules):
            if dir_only and not is_dir:
                continue
            if rx.match(rel if anchored else name):
                return not neg
        return None


# ---------- line diff ----------

def split_lines(data: bytes) -> list[bytes]:
    lines = data.split(b"\n")
    if lines and lines[-1] == b"":
        lines.pop()
        return [l + b"\n" for l in lines]
    return [l + b"\n" for l in lines[:-1]] + [lines[-1]]


def myers(a: list, b: list, check=None) -> list[tuple[int, int, int, int]]:
    """Changed regions [(a_start, a_end, b_start, b_end)] turning a into b (fewest changes), each slid as far down as it
    can go."""
    pre = 0
    while pre < len(a) and pre < len(b) and a[pre] == b[pre]:
        pre += 1
    suf = 0
    while suf < len(a) - pre and suf < len(b) - pre and a[-1 - suf] == b[-1 - suf]:
        suf += 1
    A, B = a[pre:len(a) - suf], b[pre:len(b) - suf]
    n, m = len(A), len(B)
    removed, added = [False] * len(a), [False] * len(b)
    if n == 0 or m == 0:
        for i in range(n):
            removed[pre + i] = True
        for j in range(m):
            added[pre + j] = True
    else:
        mx = n + m
        off = mx + 1
        v = [-1] * (2 * mx + 3)
        v[off + 1] = 0
        trace = []
        steps = 0
        done = False
        for d in range(mx + 1):
            trace.append(v[off - d - 1:off + d + 2])  # V before step d, for k in -d-1..d+1
            for k in range(-d, d + 1, 2):
                if k == -d or (k != d and v[off + k - 1] < v[off + k + 1]):
                    x = v[off + k + 1]
                else:
                    x = v[off + k - 1] + 1
                y = x - k
                while x < n and y < m and A[x] == B[y]:
                    x += 1
                    y += 1
                v[off + k] = x
                if x >= n and y >= m:
                    done = True
                    break
            if done:
                break
            steps += d + 1
            if steps > DIFF_BUDGET:
                break
            if check and d % 64 == 0:
                check()
        if not done:
            for i in range(n):
                removed[pre + i] = True
            for j in range(m):
                added[pre + j] = True
        else:
            x, y = n, m
            for d in range(len(trace) - 1, 0, -1):
                vv = trace[d]
                k = x - y
                if k == -d or (k != d and vv[k - 1 + d + 1] < vv[k + 1 + d + 1]):
                    pk = k + 1
                else:
                    pk = k - 1
                px = vv[pk + d + 1]
                py = px - pk
                if pk == k + 1:
                    added[pre + py] = True  # a step down: B[py] inserted
                else:
                    removed[pre + px] = True  # a step right: A[px] removed
                x, y = px, py
    _slide(a, removed)
    _slide(b, added)
    return _regions(removed, added)


def _slide(lines: list, changed: list) -> None:
    """Moves each run of changed lines down while the line after it equals its first line (git's compaction)."""
    i, n = 0, len(lines)
    while i < n:
        if not changed[i]:
            i += 1
            continue
        j = i
        while j < n and changed[j]:
            j += 1
        while j < n and not changed[j] and lines[i] == lines[j]:
            changed[i], changed[j] = False, True
            i += 1
            j += 1
            while j < n and changed[j]:
                j += 1
        i = j


def _regions(removed: list, added: list) -> list[tuple[int, int, int, int]]:
    out, i, j = [], 0, 0
    n, m = len(removed), len(added)
    while i < n or j < m:
        if i < n and j < m and not removed[i] and not added[j]:
            i += 1
            j += 1
            continue
        si, sj = i, j
        while i < n and removed[i]:
            i += 1
        while j < m and added[j]:
            j += 1
        if si == i and sj == j:  # unequal lengths left over: should not happen
            i, j = min(i + 1, n), min(j + 1, m)
            continue
        out.append((si, i, sj, j))
    return out


FUNCNAME = re.compile(rb"[A-Za-z_$]")


def timed(pattern: str, flags: int = 0):
    """A pattern from the agent or the repository, matched under the call's time limit (TimedPattern)."""
    from ..shell import TimedPattern
    return TimedPattern(re.compile(pattern, flags))


def unified(a: list[bytes], b: list[bytes], context: int, regions=None, check=None) -> list[str]:
    """Hunks as text lines ('@@ ... @@', ' x', '-x', '+x', '\\ No newline at end of file')."""
    regions = myers(a, b, check) if regions is None else regions
    out = []
    g = 0
    while g < len(regions):
        h = g
        while h + 1 < len(regions) and regions[h + 1][0] - regions[h][1] <= 2 * context:
            h += 1
        a1 = max(0, regions[g][0] - context)
        a2 = min(len(a), regions[h][1] + context)
        b1 = max(0, regions[g][2] - context)
        b2 = min(len(b), regions[h][3] + context)
        func = ""
        for k in range(a1 - 1, -1, -1):
            if FUNCNAME.match(a[k]):
                func = " " + a[k].rstrip(b"\n").decode("utf-8", "surrogateescape").rstrip()[:80].rstrip()
                break

        def rng(s, e):
            cnt = e - s
            start = s + 1 if cnt else s
            return f"{start}" if cnt == 1 else f"{start},{cnt}"
        out.append(f"@@ -{rng(a1, a2)} +{rng(b1, b2)} @@{func}")
        i, j = a1, b1
        for k in range(g, h + 1):
            s1, e1, s2, e2 = regions[k]
            while i < s1:
                _line(out, " ", a[i])
                i += 1
                j += 1
            for x in range(s1, e1):
                _line(out, "-", a[x])
            for y in range(s2, e2):
                _line(out, "+", b[y])
            i, j = e1, e2
        while i < a2:
            _line(out, " ", a[i])
            i += 1
        g = h + 1
    return out


def _line(out, sign, line: bytes) -> None:
    text = line.decode("utf-8", "surrogateescape")
    if text.endswith("\n"):
        out.append(sign + text[:-1])
    else:
        out.append(sign + text)
        out.append("\\ No newline at end of file")


def is_binary(data: bytes) -> bool:
    return b"\0" in data[:8000]


def count_changes(a: bytes, b: bytes, check=None) -> tuple[int, int]:
    la, lb = split_lines(a), split_lines(b)
    regs = myers(la, lb, check)
    return sum(r[3] - r[2] for r in regs), sum(r[1] - r[0] for r in regs)


# ---------- three-way line merge ----------

def merge_lines(base: bytes, ours: bytes, theirs: bytes, our_label: str, their_label: str, check=None) -> tuple[bytes, bool]:
    """(merged bytes, conflicted). Changes from both sides that overlap or touch become a conflict block."""
    o, a, b = split_lines(base), split_lines(ours), split_lines(theirs)
    ra = [(s, e, x, y, 0) for s, e, x, y in myers(o, a, check)]
    rb = [(s, e, x, y, 1) for s, e, x, y in myers(o, b, check)]
    allr = sorted(ra + rb, key=lambda r: (r[0], r[1]))
    out, pos, conflict, i = [], 0, False, 0
    while i < len(allr):
        s, e = allr[i][0], allr[i][1]
        group = [allr[i]]
        j = i + 1
        while j < len(allr) and allr[j][0] <= e:  # overlapping or touching changes go together
            e = max(e, allr[j][1])
            group.append(allr[j])
            j += 1
        out += o[pos:s]
        sides = {r[4] for r in group}
        if len(sides) == 1:
            side = a if 0 in sides else b
            # one side only: replay its regions in order
            p = s
            for r in group:
                out += o[p:r[0]]
                out += side[r[2]:r[3]]
                p = r[1]
            out += o[p:e]
        else:
            va = _side_text(o, a, [r for r in group if r[4] == 0], s, e)
            vb = _side_text(o, b, [r for r in group if r[4] == 1], s, e)
            if va == vb:
                out += va
            else:
                # trim lines both sides agree on at the edges
                k = 0
                while k < len(va) and k < len(vb) and va[k] == vb[k]:
                    k += 1
                t = 0
                while t < len(va) - k and t < len(vb) - k and va[-1 - t] == vb[-1 - t]:
                    t += 1
                out += va[:k]
                mid_a, mid_b = va[k:len(va) - t], vb[k:len(vb) - t]
                conflict = True
                out.append(f"<<<<<<< {our_label}\n".encode())
                out += _terminated(mid_a)
                out.append(b"=======\n")
                out += _terminated(mid_b)
                out.append(f">>>>>>> {their_label}\n".encode())
                out += va[len(va) - t:]
        pos = e
        i = j
    out += o[pos:]
    return b"".join(out), conflict


def _terminated(lines):
    return lines[:-1] + [lines[-1] + b"\n"] if lines and not lines[-1].endswith(b"\n") else lines


def _side_text(o, side, regs, s, e):
    out, p = [], s
    for r in regs:
        out += o[p:r[0]]
        out += side[r[2]:r[3]]
        p = r[1]
    out += o[p:e]
    return out


# ---------- quoting ----------

def c_quote(path: str) -> str:
    """A path as git prints it: in double quotes with C escapes when it has control, quote, backslash or non-ASCII bytes."""
    raw = path.encode("utf-8", "surrogateescape")
    if not any(c < 0x20 or c >= 0x7F or c in (0x22, 0x5C) for c in raw):
        return path
    out = []
    for c in raw:
        if c in (0x22, 0x5C):
            out.append("\\" + chr(c))
        elif c in (7, 8, 9, 10, 11, 12, 13):
            out.append("\\" + "abtnvfr"[c - 7])
        elif c < 0x20 or c >= 0x7F:
            out.append(f"\\{c:03o}")
        else:
            out.append(chr(c))
    return '"' + "".join(out) + '"'
