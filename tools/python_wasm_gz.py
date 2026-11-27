#!/usr/bin/env python3
"""Writes assets/python-wasm/python.wasm.gz from the vendored python.wasm.xz.

.NET and JavaScript have gzip but no xz, so the C# add-on (Topwater.Python, which embeds the file) and the TypeScript
one (topwater-python, whose prepack copies it) carry the module recompressed. The bytes are reproducible: zlib level 9,
no file name, mtime 0, OS byte 255 (GzipFile writes it on every platform). Both add-ons still check the decompressed
module's sha256 when they load it, so this file is a container, not a second source of truth.

    python3 tools/python_wasm_gz.py           write it
    python3 tools/python_wasm_gz.py --check   fail unless the file there is what this writes
"""
import gzip
import hashlib
import io
import lzma
import sys
from pathlib import Path

ASSETS = Path(__file__).resolve().parents[1] / "assets" / "python-wasm"
XZ_SHA256 = "93b66c1e1357a3b77481fee8e14448b55ca83a0dec4b05ec0284b33b58099b75"  # MANIFEST
WASM_SHA256 = "71df1f2cf7b3dbc0ee56b7fc58243f6ae17d9d57cdbb50855598241a7a357600"  # the decompressed module


def build() -> bytes:
    xz = (ASSETS / "python.wasm.xz").read_bytes()
    if hashlib.sha256(xz).hexdigest() != XZ_SHA256:
        sys.exit("python.wasm.xz: not the vendored file (its sha256 differs)")
    wasm = lzma.decompress(xz)
    if hashlib.sha256(wasm).hexdigest() != WASM_SHA256:
        sys.exit("python.wasm.xz: decompresses to an unexpected module")
    out = io.BytesIO()
    with gzip.GzipFile(filename="", mode="wb", compresslevel=9, fileobj=out, mtime=0) as f:
        f.write(wasm)
    return out.getvalue()


def main(argv) -> int:
    out = ASSETS / "python.wasm.gz"
    data = build()
    if "--check" in argv:
        if not out.is_file() or out.read_bytes() != data:
            print(f"{out.name}: not what tools/python_wasm_gz.py writes: run it", file=sys.stderr)
            return 1
        print(f"{out.name}: ok")
        return 0
    out.write_bytes(data)
    print(f"wrote {out} ({len(data)} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
