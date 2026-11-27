"""Writes src/Topwater.Python/python.wasm.gz from the vendored assets/python-wasm/python.wasm.xz.

.NET has no xz decoder, so the C# add-on (Topwater.Python) carries the same module recompressed with gzip, which
System.IO.Compression reads. The bytes are reproducible: zlib level 9, no file name, mtime 0. The add-on still checks
the decompressed module against the sha256 in assets/python-wasm (docs/python.md), so this file is a container, not a
second source of truth.

python3 tools/python-wasm/gzip_module.py           write it
python3 tools/python-wasm/gzip_module.py --check   fail if the checked-in file differs from what this writes
"""
import gzip
import hashlib
import io
import lzma
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "assets" / "python-wasm" / "python.wasm.xz"
TARGET = ROOT / "src" / "Topwater.Python" / "python.wasm.gz"
SOURCE_SHA256 = "93b66c1e1357a3b77481fee8e14448b55ca83a0dec4b05ec0284b33b58099b75"  # assets/python-wasm/MANIFEST
MODULE_SHA256 = "71df1f2cf7b3dbc0ee56b7fc58243f6ae17d9d57cdbb50855598241a7a357600"  # the decompressed module


def build() -> bytes:
    xz = SOURCE.read_bytes()
    if hashlib.sha256(xz).hexdigest() != SOURCE_SHA256:
        sys.exit(f"{SOURCE}: not the vendored file (its sha256 differs)")
    wasm = lzma.decompress(xz)
    if hashlib.sha256(wasm).hexdigest() != MODULE_SHA256:
        sys.exit(f"{SOURCE}: decompresses to an unexpected module")
    out = io.BytesIO()
    with gzip.GzipFile(filename="", mode="wb", compresslevel=9, fileobj=out, mtime=0) as f:
        f.write(wasm)
    return out.getvalue()


def main(argv) -> int:
    data = build()
    if "--check" in argv:
        if not TARGET.is_file() or TARGET.read_bytes() != data:
            print(f"{TARGET.relative_to(ROOT)} is not what {Path(__file__).relative_to(ROOT)} writes: run it", file=sys.stderr)
            return 1
        print("gzip_module: ok")
        return 0
    TARGET.write_bytes(data)
    print(f"wrote {TARGET.relative_to(ROOT)} ({len(data)} bytes)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
