"""Snapshots in the C# port's saved format (docs/reference.md "Snapshots"): a zip with manifest.json and blobs/<sha256>.

A snapshot saved by any port loads in the others. Loading treats the bytes as hostile, with the same limits as C#.
"""
from __future__ import annotations

import hashlib
import io
import json
import os
import re
import zipfile
from datetime import datetime, timedelta, timezone

from .parser import FunctionDef, ParseError, Parser
from .shell import SNAPSHOT_SET_OPTIONS, ShellArray
from .vfs import normalize

FORMAT_VERSION = 1
_FORMAT = "topwater-snapshot"
_INVALID = "not a valid Topwater snapshot: "
_DEFAULT_LIMITS = {"max_file_system_bytes": 128 << 20, "max_string_bytes": 32 << 20, "max_extract_entries": 100_000}


class SnapshotError(ValueError):
    """A snapshot file is malformed, hostile or over a limit."""

    def __init__(self, why: str):
        super().__init__(why if why.startswith(_INVALID) else _INVALID + why)


class SnapshotVersionError(Exception):
    """The snapshot was written in a newer format version than this Topwater reads."""


class ShellSnapshot:
    """A shell's whole machine at one moment: every file and the session. Immutable: later changes to the shell never reach
    it, and restoring it twice gives the same machine. Registered commands are not part of it.
    Take one with shell.snapshot(); put it back with shell.restore(snap)."""

    def __init__(self, entries: list, state: dict, user: str, hostname: str, created: datetime):
        self._entries, self._state = tuple(entries), state
        self.user, self.hostname = user, hostname
        self.created = created  # UTC, naive

    @property
    def entries(self) -> tuple:
        """The files, parents first: (path, is_dir, mode, owner, modified, text, data, reported_size)."""
        return self._entries

    @property
    def state(self) -> dict:
        return self._state

    # ---------- saving ----------

    def save(self, file) -> None:
        """Writes the snapshot to a path or a binary file as a zip: manifest.json and blobs/<sha256> (each content once)."""
        with zipfile.ZipFile(file, "w", zipfile.ZIP_DEFLATED, compresslevel=1) as zf:
            written: set[str] = set()
            files = []
            for path, is_dir, mode, owner, modified, text, data, reported_size in self._entries:
                entry = {"path": path, "type": "dir" if is_dir else "file", "mode": f"{mode:04o}", "owner": owner, "mtime": format_time(modified)}
                if not is_dir:
                    binary = data is not None
                    if not binary:
                        try:
                            data = text.encode("utf-8")
                        except UnicodeEncodeError:  # bytes carried in text (bytetext escapes): store them exactly
                            from . import bytetext
                            data, binary = bytetext.encode(text), True
                    _put(entry, size=len(data), binary=binary, reportedSize=reported_size)
                    if data:
                        blob = entry["blob"] = hashlib.sha256(data).hexdigest()
                        if blob not in written:
                            written.add(blob)
                            zf.writestr("blobs/" + blob, data)
                files.append(entry)
            zf.writestr("manifest.json", _dumps(self._manifest(files)))

    def _manifest(self, files: list) -> dict:
        st = self._state
        variables = []
        for n in sorted(set(st["variables"]) | set(st["arrays"]) | set(st["attributes"])):
            v = {"name": n}
            a = st["arrays"].get(n)
            _put(v, value=st["variables"].get(n), attrs=st["attributes"].get(n), exported=_is_exported(st, n),
                 array=None if a is None else _put({}, assoc=a.assoc, next=a.next, items=[[k, x] for k, x in a.pairs()]))
            variables.append(v)
        m = {"format": _FORMAT, "version": FORMAT_VERSION, "created": format_time(self.created), "user": self.user,
             "hostname": self.hostname, "cwd": st["cwd"], "files": files, "variables": variables,
             "functions": [_put({"name": n, "body": f.source}, exported=n in st["exported_functions"]) for n, f in st["functions"].items()],
             "aliases": st["aliases"], "set": list(st["set"])}
        _put(m, ifsUnset=st["ifs_unset"])
        m.update(shopt=list(st["shopt"]), traps=st["traps"], positional=st["positional"], dirStack=st["dir_stack"], history=st["history"])
        _put(m, lastExitCode=st["last_exit_code"])
        m["secondsBase"] = format_time(datetime.fromtimestamp(st["seconds_base"], timezone.utc).replace(tzinfo=None))
        _put(m, lastArg=st["last_arg"])
        m.update(pipeStatus=st["pipe_status"], getopts=list(st["getopts"]), jobs=[[p, c] for p, c in st["jobs"].items()])
        _put(m, lastBackgroundPid=st["last_bg"], nextPid=st["next_pid"])
        return m

    # ---------- loading ----------

    @staticmethod
    def load(file, limits=None) -> "ShellSnapshot":
        """Reads a snapshot (a path, a binary file or bytes) saved by any Topwater port, treating it as untrusted: the
        max_file_system_bytes, max_string_bytes and max_extract_entries of `limits` (a Shell, or any object with those
        attributes; the defaults otherwise) bound it, and nothing is written outside memory. Raises SnapshotError for
        anything malformed or over a limit, SnapshotVersionError for a newer format version."""
        o = {k: getattr(limits, k, d) for k, d in _DEFAULT_LIMITS.items()}
        try:
            return _read(file, o)
        except (SnapshotError, SnapshotVersionError, MemoryError, KeyboardInterrupt):
            raise
        except Exception as e:  # anything else wrong with the bytes (a bad zip, a bad CRC, a wrong JSON type, ...)
            raise SnapshotError(str(e) or type(e).__name__) from e


# ---------- reading ----------

_USER = re.compile(r"[A-Za-z0-9_][A-Za-z0-9_.-]{0,31}")
_HOST = re.compile(r"[A-Za-z0-9][A-Za-z0-9.-]{0,63}")
_VAR = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")
_WORD = re.compile(r"[A-Za-z0-9_+-]{1,32}")
_BLOB = re.compile(r"[0-9a-f]{64}")


def _check(value, shape: re.Pattern, what: str) -> str:
    if isinstance(value, str) and shape.fullmatch(value):
        return value
    raise SnapshotError(f"bad {what} {json.dumps(value)}")


def _int(value, what: str) -> int:
    if value is None:
        return 0
    if isinstance(value, int) and not isinstance(value, bool):
        return value
    raise SnapshotError(f"bad {what} {json.dumps(value)}")


def _list(value, what: str) -> list:
    if value is None:
        return []
    if isinstance(value, list):
        return value
    raise SnapshotError(f"{what} is not a list")


def _strings(value, what: str) -> list[str]:
    items = _list(value, what)
    if not all(isinstance(s, str) for s in items):
        raise SnapshotError(f"null in {what}")
    return list(items)


def _dict(value, what: str) -> dict[str, str]:
    if value is None:
        return {}
    if not isinstance(value, dict):
        raise SnapshotError(f"{what} is not an object")
    for k, v in value.items():
        if not isinstance(v, str):
            raise SnapshotError(f"{what} {k} has no value")
    return dict(value)


def _read_capped(src, max_bytes: int, what: str) -> bytes:
    """Reads at most max_bytes, failing past it whatever the source claims its size is."""
    out = bytearray()
    while chunk := src.read(min(1 << 16, max_bytes + 1 - len(out))):
        out += chunk
        if len(out) > max_bytes:
            raise SnapshotError(f"{what} is larger than {max_bytes} bytes")
    return bytes(out)


def _open(file, max_archive: int):
    if isinstance(file, (bytes, bytearray, memoryview)):
        file = io.BytesIO(bytes(file))
    elif isinstance(file, (str, os.PathLike)):
        if os.path.getsize(file) > max_archive:
            raise SnapshotError(f"the snapshot is larger than {max_archive} bytes")
        return open(file, "rb")
    seekable = getattr(file, "seekable", lambda: False)()
    if not seekable:
        return io.BytesIO(_read_capped(file, max_archive, "the snapshot"))
    pos = file.tell()
    if file.seek(0, io.SEEK_END) - pos > max_archive:
        raise SnapshotError(f"the snapshot is larger than {max_archive} bytes")
    file.seek(pos)
    return file


def _read(file, o: dict) -> ShellSnapshot:
    max_manifest = 4 * o["max_string_bytes"] + 1024 * o["max_extract_entries"]
    max_archive = 2 * o["max_file_system_bytes"] + max_manifest
    src = _open(file, max_archive)
    try:
        with zipfile.ZipFile(src) as zf:
            return _read_zip(zf, o, max_manifest)
    finally:
        if src is not file:
            src.close()


def _read_zip(zf: zipfile.ZipFile, o: dict, max_manifest: int) -> ShellSnapshot:
    if len(zf.infolist()) > o["max_extract_entries"] + 1:
        raise SnapshotError(f"more than {o['max_extract_entries']} entries (MaxExtractEntries)")
    names = set(zf.namelist())
    if "manifest.json" not in names:
        raise SnapshotError("manifest.json is missing")
    with zf.open("manifest.json") as f:
        raw = _read_capped(f, max_manifest, "manifest.json")
    try:
        m = json.loads(raw.decode("utf-8"))
    except ValueError:
        raise SnapshotError("manifest.json is not JSON") from None
    # the version first, so a newer format fails with that, not with whatever it changed
    if not isinstance(m, dict) or m.get("format") != _FORMAT:
        raise SnapshotError("manifest.json is not a Topwater snapshot manifest")
    version = _int(m.get("version"), "format version")
    if version > FORMAT_VERSION:
        raise SnapshotVersionError(f"snapshot format version {version} is newer than this version of Topwater supports "
                                   f"({FORMAT_VERSION}); upgrade Topwater to load it")
    if version < 1:
        raise SnapshotError(f"unknown format version {version}")
    user = _check(m.get("user"), _USER, "user")
    hostname = _check(m.get("hostname"), _HOST, "hostname")
    entries = _read_files(zf, names, _list(m.get("files"), "files"), o)
    state = _read_state(m, o)
    return ShellSnapshot(entries, state, user, hostname, parse_time(m.get("created"), "created"))


def _read_files(zf: zipfile.ZipFile, names: set, files: list, o: dict) -> list:
    if len(files) > o["max_extract_entries"]:
        raise SnapshotError(f"more than {o['max_extract_entries']} files and directories (MaxExtractEntries)")
    total = 0
    for f in files:
        if not isinstance(f, dict):
            raise SnapshotError("a file entry is not an object")
        if f.get("type") == "file":
            size = _int(f.get("size"), f"{f.get('path')}: size")
            if size < 0:
                raise SnapshotError(f"{f.get('path')}: negative size")
            total += size
        if total > o["max_file_system_bytes"]:
            raise SnapshotError(f"the files hold more than {o['max_file_system_bytes']} bytes (MaxFileSystemBytes)")
    entries, seen, dirs = [], set(), {"/"}
    cache: dict[tuple[str, bool], object] = {}  # identical contents load once and are shared, as in memory
    for f in files:
        path = f.get("path")
        if not isinstance(path, str) or not 0 < len(path) <= 4096 or path[0] != "/" or "\0" in path or normalize("/", path) != path:
            raise SnapshotError(f"bad path {json.dumps(path)}")
        mode = f.get("mode")
        if not isinstance(mode, str) or not re.fullmatch(r"[0-7]{1,4}", mode):
            raise SnapshotError(f"{path}: bad mode")
        owner = _check(f.get("owner"), _USER, path + ": owner")
        mtime = parse_time(f.get("mtime"), path + ": mtime")
        kind = f.get("type")
        text = data = reported = None
        if kind == "file":
            if path == "/":
                raise SnapshotError("/: Not a directory")
            size, binary = _int(f.get("size"), "size"), f.get("binary") is True
            reported = f.get("reportedSize")
            if reported is not None and _int(reported, path + ": reportedSize") < 0:
                raise SnapshotError(f"{path}: negative reportedSize")
            if size == 0:
                text, data = (None, b"") if binary else ("", None)
            else:
                blob = _check(f.get("blob"), _BLOB, path + ": blob")
                content = cache.get((blob, binary))
                if content is None:
                    if "blobs/" + blob not in names:
                        raise SnapshotError(f"{path}: blob {blob} is missing")
                    with zf.open("blobs/" + blob) as s:
                        b = _read_capped(s, size, "blob " + blob)
                    if len(b) != size:
                        raise SnapshotError(f"{path}: blob {blob} holds {len(b)} bytes, not {size}")
                    if hashlib.sha256(b).hexdigest() != blob:
                        raise SnapshotError(f"{path}: blob {blob} doesn't match its hash")
                    try:
                        content = cache[(blob, binary)] = b if binary else b.decode("utf-8")
                    except UnicodeDecodeError:
                        raise SnapshotError(f"{path}: text is not valid UTF-8") from None
                if binary:
                    data = content
                else:
                    text = content
        elif kind != "dir":
            raise SnapshotError(f"{path}: unknown type {json.dumps(kind)}")
        if path in seen:
            raise SnapshotError(f"{path}: File exists")
        seen.add(path)
        if path != "/" and (path.rsplit("/", 1)[0] or "/") not in dirs:
            raise SnapshotError(f"{path}: parent is not a directory")
        if kind == "dir":
            dirs.add(path)
        entries.append((path, kind == "dir", int(mode, 8), owner, mtime, text, data, reported))
    return entries


def _read_state(m: dict, o: dict) -> dict:
    limit = o["max_string_bytes"]
    chars = 0

    def charge(what: str, n: int) -> None:
        nonlocal chars
        chars += n
        if chars > limit:
            raise SnapshotError(f"{what}: variables hold more than {limit} characters (MaxStringBytes)")

    variables, attributes, arrays, unexported = {}, {}, {}, set()
    for v in _list(m.get("variables"), "variables"):
        name = _check(v.get("name") if isinstance(v, dict) else None, _VAR, "variable name")
        if name in variables or name in arrays or name in attributes:
            raise SnapshotError(f"variable {name} appears twice")
        attrs = v.get("attrs")
        if attrs:
            if not isinstance(attrs, str) or not re.fullmatch(r"[A-Za-z]{1,16}", attrs):
                raise SnapshotError(f"{name}: bad attributes")
            attributes[name] = attrs
        if not v.get("exported"):
            unexported.add(name)
        value = v.get("value")
        if value is not None:
            if not isinstance(value, str):
                raise SnapshotError(f"{name}: bad value")
            charge(name, len(name) + len(value))
            variables[name] = value
        a = v.get("array")
        if a is not None:
            if not isinstance(a, dict):
                raise SnapshotError(f"{name}: bad array")
            arr = ShellArray(a.get("assoc") is True)
            for item in _list(a.get("items"), f"{name} items"):
                if not (isinstance(item, list) and len(item) == 2 and all(isinstance(x, str) for x in item)):
                    raise SnapshotError(f"{name}: an element is not a [key, value] pair")
                key, value = item
                if not arr.assoc and not re.fullmatch(r"0|[1-9][0-9]{0,18}", key):
                    raise SnapshotError(f"{name}: bad index {json.dumps(key)}")
                if key in arr.items:
                    raise SnapshotError(f"{name}: element {json.dumps(key)} appears twice")
                charge(name, len(key) + len(value))
                arr.set(key, value)
            nxt = _int(a.get("next"), f"{name}: next")
            if not arr.assoc:
                if nxt < arr.next:
                    raise SnapshotError(f"{name}: next is below the highest index")
                arr.next = nxt
            arrays[name] = arr

    functions, exported_functions, function_chars = {}, set(), 0
    for f in _list(m.get("functions"), "functions"):
        name, body = (f.get("name"), f.get("body")) if isinstance(f, dict) else (None, None)
        if not isinstance(name, str) or not name or not isinstance(body, str):
            raise SnapshotError("a function without a name or body")
        function_chars += len(name) + len(body)
        if function_chars > limit:
            raise SnapshotError(f"functions hold more than {limit} characters (MaxStringBytes)")
        # the body is parsed again, as `name() body`, and must come back as exactly that one function
        try:
            script = Parser.parse(f"{name}() {body}\n")
        except ParseError as e:
            raise SnapshotError(f"function {name}: {e}") from None
        item = script.items[0] if len(script.items) == 1 else None
        p = item.first if item is not None else None
        if (p is None or item.rest or item.background or p.negate or p.timed or len(p.commands) != 1
                or not isinstance(p.commands[0], FunctionDef) or p.commands[0].name != name):
            raise SnapshotError(f"function {json.dumps(name)} is not one function definition")
        if name in functions:
            raise SnapshotError(f"function {name} appears twice")
        functions[name] = p.commands[0]
        if f.get("exported"):
            exported_functions.add(name)

    aliases = _dict(m.get("aliases"), "aliases")
    for k in aliases:
        if not k or re.search(r"[\s=/\0]", k):
            raise SnapshotError(f"bad alias {json.dumps(k)}")
    traps = _dict(m.get("traps"), "traps")
    for k in traps:
        _check(k, _WORD, "trap signal")
    set_options = _strings(m.get("set"), "set")
    for s in set_options:
        if s not in SNAPSHOT_SET_OPTIONS:
            raise SnapshotError(f"unknown set option {json.dumps(s)}")
    shopts = _strings(m.get("shopt"), "shopt")
    for s in shopts:
        _check(s, _WORD, "shopt name")
    cwd = m.get("cwd")
    if not isinstance(cwd, str) or not cwd or cwd[0] != "/" or "\0" in cwd or normalize("/", cwd) != cwd:
        raise SnapshotError("bad cwd")

    def code(n) -> bool:
        return isinstance(n, int) and not isinstance(n, bool) and 0 <= n <= 255

    pipe_status = _list(m.get("pipeStatus"), "pipeStatus")
    if not code(m.get("lastExitCode", 0)) or not all(code(c) for c in pipe_status):
        raise SnapshotError("exit codes must be 0-255")
    g = m.get("getopts")
    if g is not None and not (isinstance(g, list) and len(g) == 2 and all(_int(x, "getopts") >= 0 for x in g)):
        raise SnapshotError("bad getopts state")
    jobs = {}
    for j in _list(m.get("jobs"), "jobs"):
        if not (isinstance(j, list) and len(j) == 2 and isinstance(j[0], int) and code(j[1])) or j[0] in jobs:
            raise SnapshotError("bad job")
        jobs[j[0]] = j[1]
    # clock and random belong to a deterministic shell, which this port doesn't have: checked, then left unused
    if m.get("clock") is not None:
        parse_time(m["clock"], "clock")
    if m.get("random") is not None and not (isinstance(m["random"], str) and re.fullmatch(r"[0-9]{1,20}", m["random"]) and int(m["random"]) < 1 << 64):
        raise SnapshotError("bad random state")
    last_arg = m.get("lastArg")
    return {
        "cwd": cwd, "variables": variables, "attributes": attributes, "unexported": frozenset(unexported), "arrays": arrays,
        "functions": functions, "exported_functions": frozenset(exported_functions), "aliases": aliases, "traps": traps,
        "set": set_options, "ifs_unset": m.get("ifsUnset") is True, "shopt": shopts,
        "positional": _strings(m.get("positional"), "positional"), "dir_stack": _strings(m.get("dirStack"), "dirStack"),
        "history": _strings(m.get("history"), "history"), "last_exit_code": m.get("lastExitCode", 0),
        "seconds_base": parse_time(m.get("secondsBase"), "secondsBase").replace(tzinfo=timezone.utc).timestamp(),
        "last_arg": last_arg if isinstance(last_arg, str) else "", "pipe_status": list(pipe_status),
        "getopts": tuple(g) if g else (0, 1), "jobs": jobs,
        "last_bg": _int(m.get("lastBackgroundPid"), "lastBackgroundPid"), "next_pid": _int(m.get("nextPid"), "nextPid"),
    }


# ---------- helpers ----------

def _is_exported(st: dict, name: str) -> bool:
    return "x" in st["attributes"].get(name, "") or (name in st["variables"] and name not in st["unexported"])


def _put(d: dict, **values) -> dict:
    """Adds the values that aren't a default (None, False, 0), as C#'s serializer leaves those out."""
    for k, v in values.items():
        if v is not None and v is not False and not (type(v) is int and v == 0):
            d[k] = v
    return d


def _dumps(m: dict) -> bytes:
    try:
        return json.dumps(m, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    except UnicodeEncodeError:  # a lone surrogate (bytes carried in a string): escaped, still valid JSON
        return json.dumps(m, separators=(",", ":")).encode("utf-8")


def format_time(t: datetime) -> str:
    """UTC, ISO 8601, 100 ns digits, as C# writes it. Naive datetimes are UTC."""
    if t.tzinfo is not None:
        t = t.astimezone(timezone.utc).replace(tzinfo=None)
    return f"{t.year:04d}-{t:%m-%dT%H:%M:%S}.{t.microsecond:06d}0Z"   # %Y is not zero-padded below year 1000 on Linux


_TIME = re.compile(r"(\d{4})-(\d\d)-(\d\d)[T ](\d\d):(\d\d)(?::(\d\d)(?:\.(\d{1,7}))?)?(Z|[+-]\d\d:?\d\d)?")


def parse_time(s, what: str) -> datetime:
    """An ISO 8601 time as a naive UTC datetime; no zone means UTC."""
    m = _TIME.fullmatch(s.strip()) if isinstance(s, str) else None
    if not m:
        raise SnapshotError(f"bad {what} {json.dumps(s)}")
    try:
        t = datetime(int(m[1]), int(m[2]), int(m[3]), int(m[4]), int(m[5]), int(m[6] or 0), int((m[7] or "0").ljust(7, "0")[:6]))
        if m[8] and m[8] != "Z":
            sign = -1 if m[8][0] == "-" else 1
            t -= sign * timedelta(hours=int(m[8][1:3]), minutes=int(m[8][-2:]))
    except (ValueError, OverflowError):
        raise SnapshotError(f"bad {what} {json.dumps(s)}") from None
    return t
