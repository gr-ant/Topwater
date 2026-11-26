#!/usr/bin/env python3
"""Writes tests/git/packs.json: crafted packfiles (good and hostile) that all three ports' git must read or refuse the same
way. Each case is a set of files to put under .git/objects/pack (gzip, then base64), a command, and the expected exit code
plus stdout or a stderr fragment. A pack is agent-controlled input, so the hostile ones must fail fast with a clear error.

    python3 tests/git/make_packs.py
"""
import base64, gzip, hashlib, json, os, struct, zlib

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "packs.json")
TYPES = {"commit": 1, "tree": 2, "blob": 3, "tag": 4}


def oid(kind, data):
    return hashlib.sha1(f"{kind} {len(data)}\0".encode() + data).hexdigest()


def varint(n):
    out = bytearray()
    while True:
        b = n & 0x7F
        n >>= 7
        out.append(b | (0x80 if n else 0))
        if not n:
            return bytes(out)


def header(typ, size):
    b = (typ << 4) | (size & 15)
    size >>= 4
    out = bytearray()
    while size:
        out.append(b | 0x80)
        b = size & 0x7F
        size >>= 7
    out.append(b)
    return bytes(out)


def ofs(back):
    out = [back & 0x7F]
    back >>= 7
    while back:
        back -= 1
        out.insert(0, 0x80 | (back & 0x7F))
        back >>= 7
    return bytes(out)


def delta(base_len, ops, dst=None):
    """ops: ("copy", offset, size) or ("insert", bytes)."""
    body = bytearray()
    total = 0
    for op in ops:
        if op[0] == "copy":
            _, off, size = op
            cmd, args = 0x80, bytearray()
            for k in range(4):
                if (off >> (8 * k)) & 0xFF:
                    cmd |= 1 << k
                    args.append((off >> (8 * k)) & 0xFF)
            for k in range(3):
                if (size >> (8 * k)) & 0xFF:
                    cmd |= 0x10 << k
                    args.append((size >> (8 * k)) & 0xFF)
            body += bytes([cmd]) + args
            total += size
        else:
            body += bytes([len(op[1])]) + op[1]
            total += len(op[1])
    return varint(base_len) + varint(total if dst is None else dst) + body


def build(entries, names=None, large=(), count=None, raw_size=None):
    """entries: dicts with kind ('blob'...|'ofs'|'ref'|int type), data, base (entry index for ofs, sha for ref), size override.
    Returns (pack, idx, [name per entry]); names default to the object ids of full objects (None for deltas: give them)."""
    pack = bytearray(b"PACK" + struct.pack(">II", 2, len(entries) if count is None else count))
    offsets = []
    for e in entries:
        off = len(pack)
        offsets.append(off)
        data = e["data"]
        size = e.get("size", len(data))
        if e["kind"] == "ofs":
            pack += header(6, size) + ofs(off - offsets[e["base"]] if "back" not in e else e["back"])
        elif e["kind"] == "ref":
            pack += header(7, size) + bytes.fromhex(e["base"])
        else:
            pack += header(TYPES.get(e["kind"], e["kind"]) if isinstance(e["kind"], str) else e["kind"], size)
        z = zlib.compress(data)
        pack += z[:e["cut"]] if "cut" in e else z
    pack += hashlib.sha1(pack).digest()
    names = names or [e["name"] for e in entries]
    order = sorted(range(len(entries)), key=lambda i: names[i])
    fan = [0] * 256
    for i in order:
        fan[int(names[i][:2], 16)] += 1
    for b in range(1, 256):
        fan[b] += fan[b - 1]
    idx = bytearray(b"\xfftOc" + struct.pack(">I", 2) + struct.pack(">256I", *fan))
    for i in order:
        idx += bytes.fromhex(names[i])
    for i in order:
        idx += struct.pack(">I", zlib.crc32(bytes(pack[offsets[i]:(offsets + [len(pack) - 20])[i + 1]])))
    big = []
    for i in order:
        if i in large:
            idx += struct.pack(">I", 0x80000000 | len(big))
            big.append(offsets[i])
        else:
            idx += struct.pack(">I", offsets[i])
    for o in big:
        idx += struct.pack(">Q", o)
    idx += pack[-20:]
    idx += hashlib.sha1(idx).digest()
    return bytes(pack), bytes(idx)


def reseal_idx(idx):
    return idx[:-20] + hashlib.sha1(idx[:-20]).digest()


def reseal_pack(pack):
    return pack[:-20] + hashlib.sha1(pack[:-20]).digest()


def blob(data):
    return {"kind": "blob", "data": data, "name": oid("blob", data)}


def enc(b):
    return base64.b64encode(gzip.compress(b, mtime=0)).decode()


CASES = []


def case(name, pack, idx, script, code, stdout=None, stderr=None):
    c = {"name": name, "files": {"pack-t.pack": enc(pack), "pack-t.idx": enc(idx)}, "script": script, "exitCode": code}
    if stdout is not None:
        c["stdout"] = stdout
    if stderr is not None:
        c["stderr"] = stderr
    CASES.append(c)


HELLO = b"hello\n" * 4
WORLD = HELLO + b"world\n"
H, W = oid("blob", HELLO), oid("blob", WORLD)
grow = delta(len(HELLO), [("copy", 0, len(HELLO)), ("insert", b"world\n")])

# readable packs
p, i = build([blob(HELLO), {"kind": "ofs", "data": grow, "base": 0, "name": W}])
case("offset delta", p, i, f"git cat-file -p {W}; git cat-file -t {W[:7]}", 0, stdout=WORLD.decode() + "blob\n")
GOOD_P, GOOD_I = p, i
p, i = build([blob(HELLO), {"kind": "ref", "data": grow, "base": H, "name": W}])
case("reference delta", p, i, f"git cat-file -p {W}", 0, stdout=WORLD.decode())
p, i = build([blob(HELLO), {"kind": "ofs", "data": grow, "base": 0, "name": W}], large=(1,))
case("64-bit offset table", p, i, f"git cat-file -s {W}", 0, stdout=f"{len(WORLD)}\n")
chain, data = [blob(HELLO)], HELLO
for n in range(100):
    new = data + b"%d\n" % n
    chain.append({"kind": "ofs", "data": delta(len(data), [("copy", 0, len(data)), ("insert", b"%d\n" % n)]), "base": n, "name": oid("blob", new)})
    data = new
p, i = build(chain)
case("100 deltas deep", p, i, f"git cat-file -p {oid('blob', data)} | tail -1", 0, stdout="99\n")

# broken indexes
case("index signature", GOOD_P, b"\x00" * 1100, f"git cat-file -p {W}", 128, stderr="corrupt: bad signature")
case("index version", GOOD_P, reseal_idx(GOOD_I[:4] + struct.pack(">I", 3) + GOOD_I[8:]), f"git cat-file -p {W}", 128, stderr="version 3")
case("index checksum", GOOD_P, GOOD_I[:-1] + bytes([GOOD_I[-1] ^ 1]), f"git cat-file -p {W}", 128, stderr="checksum mismatch")
case("index truncated", GOOD_P, GOOD_I[:1100], f"git cat-file -p {W}", 128, stderr="corrupt")
bad = bytearray(GOOD_I)
bad[8 + 4 * 0x10:8 + 4 * 0x11] = struct.pack(">I", 0xFFFF)  # the fanout jumps up, then back down
case("fanout not monotonic", GOOD_P, reseal_idx(bytes(bad)), f"git cat-file -p {W}", 128, stderr="corrupt")
bad = bytearray(GOOD_I)
bad[1032:1072] = bad[1052:1072] + bad[1032:1052]
case("names out of order", GOOD_P, reseal_idx(bytes(bad)), f"git cat-file -p {W}", 128, stderr="corrupt")
bad = bytearray(GOOD_I)
bad[1032 + 48:1032 + 52] = struct.pack(">I", len(GOOD_P) + 100)
case("offset past the end", GOOD_P, reseal_idx(bytes(bad)), f"git cat-file -p {W}", 128, stderr="offset out of range")
bad = bytearray(GOOD_I)
bad[1032 + 48:1032 + 52] = struct.pack(">I", 0x80000005)
case("64-bit offset index out of range", GOOD_P, reseal_idx(bytes(bad)), f"git cat-file -p {W}", 128, stderr="offset out of range")
case("huge object count", GOOD_P, reseal_idx(GOOD_I[:8] + struct.pack(">256I", *([0] * 255 + [0x7FFFFFFF])) + GOOD_I[1032:]),
     f"git cat-file -p {W}", 128, stderr="corrupt")

# broken packs
case("pack checksum", GOOD_P[:20] + b"X" + GOOD_P[21:], GOOD_I, f"git cat-file -p {W}", 128, stderr="checksum mismatch")
case("pack signature", reseal_pack(b"KCAP" + GOOD_P[4:]), GOOD_I, f"git cat-file -p {W}", 128, stderr="bad signature")
case("pack count differs", reseal_pack(GOOD_P[:8] + struct.pack(">I", 3) + GOOD_P[12:]), GOOD_I, f"git cat-file -p {W}", 128, stderr="does not match")
p, i = build([blob(HELLO), {"kind": "ofs", "data": grow, "base": 0, "name": W}])
case("pack from another index", reseal_pack(p[:-20] + b"\0"), i, f"git cat-file -p {W}", 128, stderr="does not match")
case("pack too short", b"PACK", GOOD_I, f"git cat-file -p {W}", 128, stderr="corrupt")

# broken entries: each must fail as a corrupt object, quickly
p, i = build([blob(HELLO), {"kind": "ofs", "data": grow, "base": 0, "back": 0, "name": W}])
case("offset delta onto itself", p, i, f"git cat-file -p {W}", 128, stderr=f"object {W} is corrupt")
p, i = build([blob(HELLO), {"kind": "ofs", "data": grow, "base": 0, "back": 1 << 40, "name": W}])
case("offset delta before the pack", p, i, f"git cat-file -p {W}", 128, stderr="is corrupt")
A, B = "a" * 40, "b" * 40
p, i = build([{"kind": "ref", "data": grow, "base": B, "name": A}, {"kind": "ref", "data": grow, "base": A, "name": B}])
case("reference delta cycle", p, i, f"git cat-file -p {A}", 128, stderr="is corrupt")
p, i = build([{"kind": "ref", "data": grow, "base": A, "name": A}])
case("reference delta onto itself", p, i, f"git cat-file -p {A}", 128, stderr="is corrupt")
p, i = build([{"kind": "ref", "data": grow, "base": B, "name": A}])
case("reference delta base missing", p, i, f"git cat-file -p {A}", 128, stderr="is corrupt")
deep = [blob(b"x")] + [{"kind": "ofs", "data": delta(1, [("copy", 0, 1)]), "base": n, "name": "%040x" % (n + 1)} for n in range(5000)]
deep[0]["name"] = "0" * 40
p, i = build(deep)
case("5000 deltas deep", p, i, f"git cat-file -p {'%040x' % 5000}", 128, stderr="is corrupt")
case("size larger than stated", *build([{"kind": "blob", "data": HELLO, "size": 3, "name": H}]), f"git cat-file -p {H}", 128, stderr="is corrupt")
case("size smaller than stated", *build([{"kind": "blob", "data": HELLO, "size": 300, "name": H}]), f"git cat-file -p {H}", 128, stderr="is corrupt")
case("size beyond the limits", *build([{"kind": "blob", "data": HELLO, "size": 1 << 40, "name": H}]), f"git cat-file -p {H}", 128, stderr="is corrupt")
case("inflates without end", *build([{"kind": "blob", "data": HELLO, "cut": 8, "name": H}]), f"git cat-file -p {H}", 128, stderr="is corrupt")
bomb = b"\0" * (4 << 20)
case("zlib bomb", *build([{"kind": "blob", "data": bomb, "size": 10, "name": H}]), f"git cat-file -p {H}", 128, stderr="is corrupt")
case("unknown entry type", *build([{"kind": 5, "data": HELLO, "name": H}]), f"git cat-file -p {H}", 128, stderr="is corrupt")
case("contents do not match the name", *build([{"kind": "blob", "data": WORLD, "name": H}]), f"git cat-file -p {H}", 128, stderr=f"object {H} is corrupt")
for label, d in [("delta copies past its base", delta(len(HELLO), [("copy", 20, 100)])),
                 ("delta base size differs", delta(3, [("copy", 0, 3)])),
                 ("delta result size differs", delta(len(HELLO), [("copy", 0, 4)], dst=9)),
                 ("delta result beyond the limits", varint(len(HELLO)) + varint(1 << 40) + b"\x90\x01"),
                 ("delta opcode zero", delta(len(HELLO), []) + b"\0"),
                 ("delta truncated", delta(len(HELLO), [("insert", b"abcdef")])[:-3])]:
    case(label, *build([blob(HELLO), {"kind": "ofs", "data": d, "base": 0, "name": W}]), f"git cat-file -p {W}", 128, stderr="is corrupt")

with open(OUT, "w") as f:
    f.write("[\n" + ",\n".join(json.dumps(c, sort_keys=True) for c in CASES) + "\n]\n")
print(f"wrote {len(CASES)} cases to {OUT} ({os.path.getsize(OUT)} bytes)")
