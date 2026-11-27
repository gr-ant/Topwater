#!/usr/bin/env python3
"""Writes assets/python-wasm/python.wasm.gz from the vendored python.wasm.xz, for the TypeScript add-on (topwater-python):
JavaScript has gzip (DecompressionStream) but no xz. Both hashes are checked, the output has no name or time in its
header, so running it again gives the same module. The TypeScript loader checks the decompressed module's sha256.

    python3 tools/python_wasm_gz.py           write it
    python3 tools/python_wasm_gz.py --check   fail unless the file there decompresses to the pinned module
"""
import gzip
import hashlib
import lzma
import sys
from pathlib import Path

ASSETS = Path(__file__).resolve().parents[1] / "assets" / "python-wasm"
XZ_SHA256 = "93b66c1e1357a3b77481fee8e14448b55ca83a0dec4b05ec0284b33b58099b75"  # MANIFEST
WASM_SHA256 = "71df1f2cf7b3dbc0ee56b7fc58243f6ae17d9d57cdbb50855598241a7a357600"  # the decompressed module


def main() -> int:
    out = ASSETS / "python.wasm.gz"
    if sys.argv[1:] == ["--check"]:
        ok = hashlib.sha256(gzip.decompress(out.read_bytes())).hexdigest() == WASM_SHA256
        print(f"{out.name}: {'ok' if ok else 'does not decompress to the pinned module'}")
        return 0 if ok else 1
    xz = (ASSETS / "python.wasm.xz").read_bytes()
    if hashlib.sha256(xz).hexdigest() != XZ_SHA256:
        sys.exit("python.wasm.xz: not the vendored file (its sha256 differs)")
    wasm = lzma.decompress(xz)
    if hashlib.sha256(wasm).hexdigest() != WASM_SHA256:
        sys.exit("python.wasm.xz: decompresses to an unexpected module")
    out.write_bytes(gzip.compress(wasm, compresslevel=9, mtime=0))
    print(f"wrote {out} ({out.stat().st_size} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
