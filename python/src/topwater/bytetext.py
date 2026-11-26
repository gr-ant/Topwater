"""Carries arbitrary bytes through the shell's string pipes and redirects without loss (Python's "surrogateescape"):
valid UTF-8 decodes as usual and each other byte becomes the lone surrogate U+DC80..U+DCFF. Text is unaffected;
binary output (gzip -c, tar cf -) survives `|` and `>`, files store the original bytes, and what leaves the shell shows U+FFFD."""
import re

_ESCAPES = re.compile("[\udc80-\udcff]")
_OTHER_SURROGATES = re.compile("[\ud800-\udc7f\udd00-\udfff]")


def decode(data: bytes) -> str:
    return data.decode("utf-8", "surrogateescape")


def has_escapes(text: str | None) -> bool:
    return text is not None and _ESCAPES.search(text) is not None


def encode(text: str) -> bytes:
    """The bytes text stands for: UTF-8 with escaped bytes restored (any other lone surrogate becomes U+FFFD)."""
    try:
        return text.encode("utf-8", "surrogateescape")
    except UnicodeEncodeError:
        return _OTHER_SURROGATES.sub("�", text).encode("utf-8", "surrogateescape")


def rejoin(text: str) -> str:
    """Escaped bytes that together spell valid UTF-8 become those characters (`printf '\\xc3\\xa9'` is é); the rest stay escaped."""
    return decode(encode(text)) if has_escapes(text) else text


def printable(text: str) -> str:
    """For text leaving the shell: escaped bytes show as U+FFFD, as a lossy UTF-8 decode would."""
    return encode(text).decode("utf-8", "replace") if has_escapes(text) else text
