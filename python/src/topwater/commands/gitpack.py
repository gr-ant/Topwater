"""Reading git packfiles (.git/objects/pack/*.pack with their v2 .idx), as `git clone` and `git gc` leave them. Read only:
new objects are still written loose. Written from the publicly known formats. A pack is agent-controlled input, so both
checksums are verified on load, every offset and size is checked, delta chains are bounded and walked without recursion,
and inflated sizes stay under the shell's string limit."""
from __future__ import annotations

import bisect
import hashlib
import struct
import zlib

from .gitcore import GitError, fatal, msg

MAX_CHAIN = 4096  # deltas on top of one base (real git refuses to write deeper than 4095)
MAX_PACKS = 256
MAX_NAMES = 1_000_000  # objects named by all the packs of one repository
CACHE_BYTES = 64 << 20  # object contents one repository keeps for reuse, all caches together
KINDS = {1: "commit", 2: "tree", 3: "blob", 4: "tag"}


class ObjectCache:
    """Objects, commits and delta bases one command keeps for reuse, under a single byte budget per repository: an
    entry over a quarter of it is not kept, and the whole cache is dropped when the next entry would not fit."""

    def __init__(self, budget: int = CACHE_BYTES):
        self.items: dict = {}
        self.used, self.budget = 0, budget

    def get(self, key):
        return self.items.get(key)

    peak = 0  # the most any cache has held, for tests

    def put(self, key, value, size: int) -> None:
        if size > self.budget // 4:
            return
        if self.used + size > self.budget:
            self.items.clear()
            self.used = 0
        self.items[key] = value
        self.used += size
        ObjectCache.peak = max(ObjectCache.peak, self.used)


class Corrupt(Exception):
    """A broken object entry; the caller names the object it was looking for."""


def bad_pack(path: str, why: str, **values) -> GitError:
    return fatal("badPack", path=path, why=msg(why, **values))


class Pack:
    def __init__(self, path: str, idx: bytes, pack: bytes, limit: int, check, cache: ObjectCache):
        self.path, self.pack, self.limit, self.check, self.cache = path, pack, limit, check, cache
        ipath = path[:-5] + ".idx"
        if len(idx) < 1072 or idx[:4] != b"\xfftOc":
            raise bad_pack(ipath, "packBadSignature")
        if struct.unpack(">I", idx[4:8])[0] != 2:
            raise bad_pack(ipath, "packBadVersion", version=str(struct.unpack(">I", idx[4:8])[0]))
        fan = struct.unpack(">256I", idx[8:1032])
        n = fan[255]
        large = len(idx) - 1072 - 28 * n
        if any(fan[i] > fan[i + 1] for i in range(255)) or large < 0 or large % 8:
            raise bad_pack(ipath, "packTruncated")
        if hashlib.sha1(idx[:-20]).digest() != idx[-20:]:
            raise bad_pack(ipath, "packBadChecksum")
        if len(pack) < 32 or pack[:4] != b"PACK":
            raise bad_pack(path, "packBadSignature")
        version, count = struct.unpack(">II", pack[4:12])
        if version not in (2, 3):
            raise bad_pack(path, "packBadVersion", version=str(version))
        if hashlib.sha1(pack[:-20]).digest() != pack[-20:]:
            raise bad_pack(path, "packBadChecksum")
        if count != n or idx[-40:-20] != pack[-20:]:
            raise bad_pack(path, "packNoMatch")
        if n * 2 > len(pack) - 32:  # every entry takes at least two bytes
            raise bad_pack(ipath, "packTooMany")
        names = idx[1032:1032 + 20 * n].hex()
        self.names = [names[40 * i:40 * i + 40] for i in range(n)]
        small = struct.unpack(f">{n}I", idx[1032 + 24 * n:1032 + 28 * n])
        big = struct.unpack(f">{large // 8}Q", idx[1032 + 28 * n:len(idx) - 40])
        self.offsets: dict[str, int] = {}
        end = len(pack) - 20
        for i, name in enumerate(self.names):
            if i & 1023 == 0:
                check()
            if (i and name <= self.names[i - 1]) or fan[int(name[:2], 16)] <= i or (name[:2] != "00" and fan[int(name[:2], 16) - 1] > i):
                raise bad_pack(ipath, "packNotSorted")
            off = small[i]
            if off & 0x80000000:
                if off & 0x7FFFFFFF >= len(big):
                    raise bad_pack(ipath, "packBadOffset")
                off = big[off & 0x7FFFFFFF]
            if not 12 <= off < end:
                raise bad_pack(ipath, "packBadOffset")
            self.offsets[name] = off

    def with_prefix(self, prefix: str) -> list[str]:
        i = bisect.bisect_left(self.names, prefix)
        out = []
        while i < len(self.names) and self.names[i].startswith(prefix) and len(out) < 2:
            out.append(self.names[i])
            i += 1
        return out

    def read(self, sha: str) -> tuple[str, bytes]:
        """The object's kind and contents; Corrupt for a broken entry (the caller still verifies the object id)."""
        try:
            return self._unpack(self.offsets[sha])
        except (IndexError, struct.error, zlib.error) as e:
            raise Corrupt() from e

    def _unpack(self, off: int) -> tuple[str, bytes]:
        p = self.pack
        chain: list[tuple[int, int, int]] = []  # (entry offset, data offset, delta size), newest first
        seen: set[int] = set()
        while True:
            self.check()
            hit = self.cache.get((self.path, off))
            if hit:
                kind, data = hit
                break
            if off in seen or len(chain) >= MAX_CHAIN:
                raise Corrupt()
            seen.add(off)
            c = p[off]
            typ, size, shift, pos = (c >> 4) & 7, c & 15, 4, off + 1
            while c & 0x80:
                c = p[pos]
                pos += 1
                size |= (c & 0x7F) << shift
                shift += 7
                if shift > 70:
                    raise Corrupt()
            if size > self.limit:
                raise Corrupt()
            if typ in KINDS:
                kind, data = KINDS[typ], self._inflate(pos, size)
                self._remember(off, kind, data)
                break
            if typ == 6:  # offset delta: the base is earlier in this pack
                c = p[pos]
                pos += 1
                back = c & 0x7F
                while c & 0x80:
                    c = p[pos]
                    pos += 1
                    back = ((back + 1) << 7) | (c & 0x7F)
                    if back > off:
                        raise Corrupt()
                base = off - back
                if not 12 <= base < off:
                    raise Corrupt()
            elif typ == 7:  # reference delta: the base is named by its id (in this pack)
                base = self.offsets.get(p[pos:pos + 20].hex())
                pos += 20
                if base is None:
                    raise Corrupt()
            else:
                raise Corrupt()
            chain.append((off, pos, size))
            off = base
        for entry, pos, size in reversed(chain):
            data = apply_delta(data, self._inflate(pos, size), self.limit, self.check)
            self._remember(entry, kind, data)
        return kind, data

    def _inflate(self, pos: int, size: int) -> bytes:
        d = zlib.decompressobj()
        out = bytearray()
        view = memoryview(self.pack)[:len(self.pack) - 20]
        while not d.eof:
            self.check()
            chunk = d.unconsumed_tail
            if not chunk:
                chunk = view[pos:pos + 65536]
                pos += len(chunk)
                if not chunk:
                    raise Corrupt()
            out += d.decompress(chunk, size + 1 - len(out))  # never more than one byte past the stated size
            if len(out) > size:
                raise Corrupt()
        if len(out) != size:
            raise Corrupt()
        return bytes(out)

    def _remember(self, off: int, kind: str, data: bytes) -> None:
        self.cache.put((self.path, off), (kind, data), len(data))


def _varint(d: bytes, i: int) -> tuple[int, int]:
    v = shift = 0
    while True:
        c = d[i]
        i += 1
        v |= (c & 0x7F) << shift
        shift += 7
        if not c & 0x80:
            return v, i
        if shift > 63:
            raise Corrupt()


def apply_delta(base: bytes, delta: bytes, limit: int, check) -> bytes:
    """Rebuilds an object from its base and a delta: a header (base size, result size), then copy and insert steps."""
    src, i = _varint(delta, 0)
    dst, i = _varint(delta, i)
    if src != len(base) or dst > limit:
        raise Corrupt()
    out = bytearray()
    n, steps = len(delta), 0
    while i < n:
        steps += 1
        if steps & 0xFFF == 0:
            check()
        c = delta[i]
        i += 1
        if c & 0x80:  # copy from the base: offset and size bytes present per bit
            off = size = 0
            for k in range(4):
                if c & (1 << k):
                    off |= delta[i] << (8 * k)
                    i += 1
            for k in range(3):
                if c & (0x10 << k):
                    size |= delta[i] << (8 * k)
                    i += 1
            size = size or 0x10000
            if off + size > len(base) or len(out) + size > dst:
                raise Corrupt()
            out += base[off:off + size]
        elif c:  # insert the next c bytes of the delta
            if i + c > n or len(out) + c > dst:
                raise Corrupt()
            out += delta[i:i + c]
            i += c
        else:
            raise Corrupt()
    if len(out) != dst:
        raise Corrupt()
    return bytes(out)
