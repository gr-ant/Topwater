"""Recordings: a shell's calls with what they returned, saved in a .topwater file (spec/topwater-file) next to the machine.

A .topwater file is a zip: manifest.json (the machine after the last call), blobs/<sha256> shared by every manifest,
history.jsonl (a header, then one line per call) and checkpoints/<n>.json (the machine after n calls; 0 is the start).
Recording.load also reads a snapshot zip (a recording without calls) and the JSON Lines recordings Topwater wrote before.
"""
from __future__ import annotations

import base64
import binascii
import hashlib
import inspect
import io
import json
import math
import os
import re
import zipfile
from dataclasses import dataclass, field
from datetime import datetime

from .shell import Shell, ShellResult
from .snapshot import (_HOST, _USER, ShellSnapshot, SnapshotError, SnapshotVersionError, _check_entry_count, _dumps,
                       _check_header, _is_int, _manifest_bytes, _manifest_json, _max_archive, _open, _open_zip, _put, _read, _read_capped, _read_manifest, _BLOB,
                       _read_outline, _save_zip, format_time, parse_time)
from .vfs import utc_now
from .webaccess import ERROR_KINDS, WebExchange, WebPolicy, WebSession

HISTORY_VERSION = 1
MAX_CHECKPOINTS = 1000  # the start included
MAX_CHECKPOINTS_FILE_SYSTEMS = 4  # all checkpoints' files together: at most this many times max_file_system_bytes,
# and this many times max_extract_entries entries

# the Shell() arguments a recording keeps, with their names in the file
_OPTIONS = {"max_loop_iterations": "maxLoopIterations", "max_extract_bytes": "maxExtractBytes",
            "max_extract_entries": "maxExtractEntries", "max_call_duration": "maxCallDurationMs",
            "max_file_system_bytes": "maxFileSystemBytes", "max_string_bytes": "maxStringBytes"}
_DEFAULTS = {k: p.default for k, p in inspect.signature(Shell).parameters.items() if k in _OPTIONS or k in ("user", "hostname")}
_CHECKPOINT = re.compile(r"checkpoints/(0|[1-9][0-9]{0,8})\.json")
_SIDECAR = re.compile(r"[A-Za-z0-9._-]+(/[A-Za-z0-9._-]+)*")
_INVALID = "not a valid Topwater recording: "


class RecordingError(SnapshotError):
    """A recording is malformed, hostile or over a limit. A SnapshotError (so a ValueError) too: a machine inside it can fail
    with one of those."""

    def __init__(self, why: str):
        ValueError.__init__(self, why if why.startswith(_INVALID) else _INVALID + why)


@dataclass
class RecordedCall:
    """One call and what it returned. elapsed_ms is wall time, never compared; time is the wall clock when it started (UTC,
    naive); files the paths it added, changed or removed; snapshot the machine right after it, when one was kept."""
    index: int
    command: str
    stdout: str
    stderr: str
    exit_code: int
    trailer: str | None = None
    elapsed_ms: float = 0.0
    time: datetime | None = None
    files: list[str] = field(default_factory=list)
    snapshot: ShellSnapshot | None = None
    web: list[WebExchange] | None = None  # the web requests the call made and what came back, in order


@dataclass(frozen=True)
class ReplayDifference:
    index: int
    command: str
    field: str  # "stdout", "stderr", "exitCode" or "trailer"
    expected: str
    actual: str


@dataclass(frozen=True)
class ReplayReport:
    differences: list[ReplayDifference]

    @property
    def matches(self) -> bool:
        return not self.differences


class Recording:
    """A recorded session: the starting machine and options, then every call. `options` are Shell() arguments (user,
    hostname and the limits; Shell's defaults for any left out). start_time and random_seed come from files made on a
    deterministic shell, which this port doesn't have: kept and written back, never used."""

    def __init__(self, start: ShellSnapshot, options: dict | None = None):
        self.start = start
        self.options = {**_DEFAULTS, **(options or {})}
        self.start_time: datetime | None = None
        self.random_seed: int | None = None
        self.created: datetime | None = utc_now()
        self.topwater: str | None = "python"
        self.metadata: dict[str, str] = {}
        self.app_commands: list[str] = []
        self.calls: list[RecordedCall] = []
        # the web access the shell had (Shell.allow_web), with the credentials' values and transport left out; a replay
        # serves the recorded responses under it and never touches the network
        self.web: WebPolicy | None = None

    def create_shell(self, configure=None, start: ShellSnapshot | None = None, first_call: int = 0) -> Shell:
        """A fresh shell with the recording's options, then configure(shell) (register your commands), then the start (or
        `start`). first_call: the call the shell runs first, so its web requests get that call's recorded responses."""
        shell = Shell(**self.options)
        if configure is not None:
            configure(shell)
        # the recorded responses, never the network (even if configure allowed it), and no credential is looked up
        w = self.web
        policy = WebPolicy(explain_blocked=False) if w is None else WebPolicy(
            w.domains, w.methods, w.allow_http, w.max_response_bytes, w.max_requests_per_call, w.max_requests,
            w.timeout, w.max_redirects, {h: (lambda: "") for h in w.credentials}, w.explain_blocked)
        shell._web = WebSession(policy, [x for c in self.calls[first_call:] for x in (c.web or [])])
        shell.restore(start or self.start)
        return shell

    def shell_at(self, index: int, configure=None) -> Shell:
        """The machine after call `index` (-1: before the first): the newest checkpoint at or before it, then the calls after it run again."""
        if not -1 <= index < len(self.calls):
            raise IndexError(f"no call {index} in a recording of {len(self.calls)} calls")
        at = index
        while at >= 0 and self.calls[at].snapshot is None:
            at -= 1
        shell = self.create_shell(configure, self.calls[at].snapshot if at >= 0 else None, at + 1)
        for call in self.calls[at + 1:index + 1]:
            shell.run(call.command)
        return shell

    def replay(self, shell: Shell | None = None, normalize=None) -> ReplayReport:
        """Runs every call on `shell` (default: create_shell()) and compares exit code, stdout, stderr and trailer with the
        recording, after normalize(text) on both sides when given."""
        shell = shell or self.create_shell()
        diffs = []
        for call in self.calls:
            r = shell.run(call.command)
            for name, e, a in (("exitCode", str(call.exit_code), str(r.exit_code)), ("stdout", call.stdout, r.stdout),
                               ("stderr", call.stderr, r.stderr), ("trailer", call.trailer or "", r.trailer or "")):
                if (normalize(e) != normalize(a)) if normalize else e != a:
                    diffs.append(ReplayDifference(call.index, call.command, name, e, a))
        return ReplayReport(diffs)

    # ---------- the file ----------

    def save(self, file) -> None:
        """Writes the recording to a path or a binary file as a .topwater zip."""
        checkpoints = [(0, self.start)] + [(i + 1, c.snapshot) for i, c in enumerate(self.calls) if c.snapshot is not None]
        def write(zf: zipfile.ZipFile) -> None:
            written: set[str] = set()
            checkpoints[-1][1]._write(zf, "manifest.json", written)
            zf.writestr("history.jsonl", b"".join(_dumps(line) + b"\n" for line in self._history(zf, written)))
            for n, snap in checkpoints:
                snap._write(zf, f"checkpoints/{n}.json", written)
        _save_zip(file, write)

    def _history(self, zf: zipfile.ZipFile, written: set):
        o = self.options
        options = {"user": o["user"], "hostname": o["hostname"]}
        _put(options, startTime=self.start_time and format_time(self.start_time), randomSeed=self.random_seed)
        for k, name in _OPTIONS.items():
            options[name] = o[k] if k != "max_call_duration" else -1 if math.isinf(o[k]) else round(o[k] * 1000)
        header = {"format": "topwater-history", "version": HISTORY_VERSION}
        _put(header, created=self.created and format_time(self.created), topwater=self.topwater)
        header["options"] = options
        _put(header, appCommands=self.app_commands or None, metadata=self.metadata or None)
        if self.web is not None:
            p = self.web
            header["web"] = {
                "domains": list(p.domains), "methods": list(p.methods), "allowHttp": p.allow_http, "maxResponseBytes": p.max_response_bytes,
                "maxRequestsPerCall": p.max_requests_per_call, "maxRequests": p.max_requests, "timeoutMs": int(p.timeout * 1000),
                "maxRedirects": p.max_redirects, "explainBlocked": p.explain_blocked, "credentials": sorted(p.credentials),
            }
        yield header
        for c in self.calls:
            line = {"index": c.index, "command": c.command, "stdout": c.stdout, "stderr": c.stderr, "exitCode": c.exit_code}
            _put(line, trailer=c.trailer)
            line["elapsedMs"] = round(c.elapsed_ms, 3)
            _put(line, time=c.time and format_time(c.time), files=c.files or None)
            if c.web:
                line["web"] = [_exchange_entry(x, zf, written) for x in c.web]
            yield line

    @staticmethod
    def load(file, open_snapshot=None, limits=None) -> "Recording":
        """Reads a .topwater file, a snapshot zip (a recording without calls) or an old JSON Lines recording, from a path,
        a binary file or bytes, treating it as untrusted: every machine in it is checked as ShellSnapshot.load does under
        `limits` (a Shell or any object with its limit attributes; the defaults otherwise), never under limits the file
        names, and the file's options can lower those limits for create_shell(), never raise them. An old recording's
        side-car snapshot files are opened by open_snapshot(relative_name), which returns what ShellSnapshot.load takes;
        loading from a path opens them next to the file. Raises RecordingError or SnapshotError (ValueErrors) for a bad
        file, SnapshotVersionError for a newer format."""
        o = {k: getattr(limits, k, _DEFAULTS[k]) for k in _OPTIONS}
        if open_snapshot is None and isinstance(file, (str, os.PathLike)):
            folder = os.path.dirname(os.path.abspath(file))
            open_snapshot = lambda rel: os.path.join(folder, rel)  # noqa: E731
        try:
            src = _open(file, 4 * _max_archive(o), "the recording")
            try:
                head = src.read(2)
                src.seek(-len(head), os.SEEK_CUR)
                zipped = head == b"PK"  # zips are told by their signature, not their name
                # an old recording is not compressed: its size, capped above, is what a sender must send
                return _read_topwater(src, o) if zipped else _read_jsonl(src, 4 * _max_archive(o), o, open_snapshot)
            finally:
                if src is not file:
                    src.close()
        except (SnapshotError, SnapshotVersionError, MemoryError, KeyboardInterrupt):
            raise
        except Exception as e:  # anything else wrong with the bytes (a bad zip, a bad CRC, a wrong JSON type, ...)
            raise RecordingError(str(e) or type(e).__name__) from e


class ShellRecorder:
    """Records a shell's calls until stop(); also a context manager. recording grows as calls run."""

    def __init__(self, shell: Shell, snapshot_every: int = 0, metadata: dict | None = None):
        self._shell, self._every = shell, snapshot_every
        self._changes = shell.fs.change_tracker()  # the paths each call changed, without a snapshot per call
        self._marked = False  # snapshot_now() asked to keep the last call's checkpoint
        self.recording = Recording(shell.snapshot(), {k: getattr(shell, k) for k in _DEFAULTS})
        self.recording.metadata = dict(metadata or {})
        builtins = shell._builtin_names or set()
        self.recording.app_commands = sorted(n for n in shell.commands if n not in builtins)

    def _add(self, command: str, result: ShellResult, elapsed_ms: float, started: datetime, web: list | None = None) -> None:
        calls = self.recording.calls
        if self.recording.web is None and self._shell._web is not None:
            self.recording.web = self._shell._web.policy
        if calls and not self._marked and not (self._every > 0 and len(calls) % self._every == 0):
            calls[-1].snapshot = None  # it was only kept as the end state, and the end moved on
        self._marked = False
        # the last call always has a snapshot, so a saved recording holds the end state; it is taken when first used (or at
        # stop()), since the next call usually replaces it, unless snapshot_every keeps this one.
        # shortcut: a change made to the shell outside run() after the last call, before then, is in it too; snapshot every
        # call again if a host needs that
        sh = self._shell
        keep = self._every > 0 and (len(calls) + 1) % self._every == 0
        snap = sh.snapshot() if keep else ShellSnapshot._lazy(sh.snapshot, sh.user, sh.hostname, utc_now())
        calls.append(RecordedCall(len(calls), command, result.stdout, result.stderr, result.exit_code, result.trailer,
                                  elapsed_ms, started, self._changes.changes(), snap, web or None))

    def snapshot_now(self) -> None:
        """Keeps a checkpoint of the machine now: after the last call (or as the start, before any)."""
        now = self._shell.snapshot()
        self._changes.changes()  # the next call's files are measured from here
        if self.recording.calls:
            self.recording.calls[-1].snapshot = now
            self._marked = True
        else:
            self.recording.start = now

    def stop(self) -> None:
        """Stops recording; later calls are not added."""
        if self._shell._recorder is self:
            self._shell._recorder = None
            if self.recording.calls and self.recording.calls[-1].snapshot is not None:
                self.recording.calls[-1].snapshot.entries  # noqa: B018  build the deferred end state now

    def save(self, file) -> None:
        self.recording.save(file)

    def __enter__(self) -> "ShellRecorder":
        return self

    def __exit__(self, *exc) -> None:
        self.stop()


# ---------- reading ----------

def _bad(why: str) -> RecordingError:
    return RecordingError(why)


def _options(rec_options: dict, o: dict) -> tuple[dict, datetime | None, int | None]:
    """The header's options as Shell() arguments: its limits lower the caller's, never raise them."""
    if not isinstance(rec_options, dict):
        raise _bad("options are missing")
    user, host = rec_options.get("user", _DEFAULTS["user"]), rec_options.get("hostname", _DEFAULTS["hostname"])
    if not (isinstance(user, str) and _USER.fullmatch(user)):
        raise _bad(f"bad user {json.dumps(user)}")
    if not (isinstance(host, str) and _HOST.fullmatch(host)):
        raise _bad(f"bad hostname {json.dumps(host)}")
    out = {"user": user, "hostname": host}
    for k, name in _OPTIONS.items():
        v = rec_options.get(name)
        if v is not None and not _is_int(v):
            raise _bad(f"bad {name} {json.dumps(v)}")
        if k == "max_call_duration":
            v = None if v is None or v == -1 else v / 1000  # -1: no limit
        out[k] = o[k] if v is None else min(max(v, 0), o[k])
    start_time = rec_options.get("startTime")
    seed = rec_options.get("randomSeed")
    if seed is not None and not (_is_int(seed) and -2**31 <= seed < 2**31):
        raise _bad("bad randomSeed")
    return out, None if start_time is None else parse_time(start_time, "startTime"), seed


def _header(line: str, fmt: str, version: int, what: str) -> dict:
    try:
        h = json.loads(line)
    except ValueError:
        raise _bad("the header is not JSON") from None
    if not isinstance(h, dict) or h.get("format") != fmt:
        raise _bad(f"the first line is not a {fmt} header")
    v = h.get("version")
    if not _is_int(v) or v < 1:
        raise _bad(f"unknown format version {json.dumps(v)}")
    if v > version:  # before anything that version might have changed
        raise SnapshotVersionError(f"{what} format version {v} is newer than this version of Topwater supports "
                                   f"({version}); upgrade Topwater to load it")
    return h


def _new_recording(h: dict, start: ShellSnapshot, o: dict) -> Recording:
    options, start_time, seed = _options(h.get("options"), o)
    rec = Recording(start, options)
    rec.start_time, rec.random_seed = start_time, seed
    rec.created = None if h.get("created") is None else parse_time(h["created"], "created")
    topwater = h.get("topwater")
    rec.topwater = topwater if isinstance(topwater, str) else None
    metadata = h.get("metadata") or {}
    if not isinstance(metadata, dict) or not all(isinstance(v, str) for v in metadata.values()):
        raise _bad("metadata is not an object of strings")
    apps = h.get("appCommands") or []
    if not isinstance(apps, list) or not all(isinstance(a, str) for a in apps):
        raise _bad("appCommands is not a list of strings")
    rec.metadata, rec.app_commands = dict(metadata), list(apps)
    if h.get("web") is not None:
        rec.web = _web_policy(h["web"])
    return rec


def _call(line: str, number: int, index: int) -> tuple[RecordedCall, dict]:
    try:
        e = json.loads(line)
    except ValueError:
        raise _bad(f"line {number} is not JSON") from None
    if not isinstance(e, dict):
        raise _bad(f"line {number} is not an object")
    if "index" in e and not (_is_int(e["index"]) and e["index"] >= 0):
        raise _bad(f"line {number}: bad index")
    index = e.get("index", index)  # checkpoints follow the line's position; the index is the file's own label
    command, stdout, stderr, trailer = e.get("command"), e.get("stdout", ""), e.get("stderr", ""), e.get("trailer")
    if not all(isinstance(s, str) for s in (command, stdout, stderr)) or not (trailer is None or isinstance(trailer, str)):
        raise _bad(f"line {number}: command, stdout, stderr and trailer must be strings")
    code, elapsed, files = e.get("exitCode", 0), e.get("elapsedMs", 0), e.get("files")
    files = [] if files is None else files
    if not _is_int(code):
        raise _bad(f"line {number}: bad exitCode")
    if isinstance(elapsed, bool) or not isinstance(elapsed, (int, float)) or not 0 <= elapsed <= 1e12:
        raise _bad(f"line {number}: bad elapsedMs")
    if not isinstance(files, list) or not all(isinstance(f, str) for f in files):
        raise _bad(f"line {number}: files is not a list of paths")
    t = None if e.get("time") is None else parse_time(e["time"], f"line {number}: time")
    return RecordedCall(index, command, stdout, stderr, code, trailer, float(elapsed), t, list(files)), e


def _web_policy(w) -> WebPolicy:
    """The header's policy: everything but the credentials' values and the transport."""
    if not isinstance(w, dict):
        raise _bad("bad web policy: not an object")

    def get(name, check, default):
        v = w.get(name)
        if v is None:
            return default
        if not check(v):
            raise _bad(f"bad web policy: bad {name}")
        return v
    strings = lambda v: isinstance(v, list) and all(isinstance(x, str) for x in v)  # noqa: E731
    boolean = lambda v: isinstance(v, bool)  # noqa: E731
    policy = WebPolicy(
        get("domains", strings, []), get("methods", strings, ["GET"]), get("allowHttp", boolean, False),
        get("maxResponseBytes", _is_int, 0), get("maxRequestsPerCall", _is_int, 0), get("maxRequests", _is_int, 0),
        min(max(get("timeoutMs", _is_int, 0), 0), 2**31 - 1) / 1000, get("maxRedirects", _is_int, 0),
        {h: (lambda: "") for h in get("credentials", strings, [])}, get("explainBlocked", boolean, False))
    try:
        WebSession(policy)
    except ValueError as e:
        raise _bad(f"bad web policy: {e}") from None
    return policy


def _with_web(c: RecordedCall, web, number: int, blob) -> RecordedCall:
    """The call with its line's web exchanges; blob(name, size, where) reads a response body (None: not a .topwater file)."""
    if web is None:
        return c
    if not isinstance(web, list):
        raise _bad(f"line {number}: web is not a list")
    c.web = []
    for i, x in enumerate(web):
        where = f"line {number} web {i}"
        if not isinstance(x, dict):
            raise _bad(f"line {number}: null in web" if x is None else f"{where}: not an object")
        m, u, error = x.get("method"), x.get("url"), x.get("error")
        if not (isinstance(m, str) and m and isinstance(u, str) and u):
            raise _bad(f"{where}: method and url are required")
        if error is not None:
            if error not in ERROR_KINDS:
                raise _bad(f"{where}: unknown error {json.dumps(error)}")
            c.web.append(WebExchange(m, u, error=error))
            continue
        status, size, reason, headers = x.get("status", 0), x.get("size", 0), x.get("reason"), x.get("headers")
        if (not _is_int(status) or not 100 <= status <= 999 or not _is_int(size) or size < 0 or not (reason is None or isinstance(reason, str))
                or not (headers is None or isinstance(headers, list) and all(isinstance(h, list) and len(h) == 2 and all(isinstance(v, str) for v in h)
                                                                           for h in headers))):
            raise _bad(f"{where}: bad response")
        if blob is None:
            raise _bad(f"{where}: web responses need a .topwater file")
        c.web.append(WebExchange(m, u, status, reason or "", [(k, v) for k, v in headers or []], blob(x.get("body"), size, where)))
    return c


def _exchange_entry(x: WebExchange, zf: zipfile.ZipFile, written: set) -> dict:
    """One request of a call and its response; the body is a blob (written now, so it is in the zip before history.jsonl)."""
    if x.error is not None:
        return {"method": x.method, "url": x.url, "error": x.error}
    entry = {"method": x.method, "url": x.url, "status": x.status, "reason": x.reason, "headers": [[k, v] for k, v in x.headers]}
    if x.body:
        blob = entry["body"] = hashlib.sha256(x.body).hexdigest()
        if blob not in written:
            written.add(blob)
            zf.writestr("blobs/" + blob, x.body)
    entry["size"] = len(x.body)
    return entry


def _lines(f, max_bytes: int, o: dict, what: str):
    """(line number, text) for each line of a binary stream that isn't blank, read one line at a time: a deflated file
    can expand a thousandfold, so nothing is read past max_bytes or past the last line a recording may have."""
    max_lines = o["max_extract_entries"] + 1  # the header and the calls; blank lines count too, as they cost reading
    left, n = max_bytes, 0
    while line := f.readline(left + 1):
        left -= len(line)
        if left < 0:
            raise _bad(f"{what} is larger than {max_bytes} bytes")
        n += 1
        if n > max_lines:
            raise _bad(f"more than {max_lines - 1} calls (MaxExtractEntries)")
        line = line.rstrip(b"\n")
        if line or n == 1:
            yield n, line.decode("utf-8")


def _file_budget(o: dict, what: str):
    """count_files for every machine of one file: their entries together are capped, counted before any is built."""
    cap, total = MAX_CHECKPOINTS_FILE_SYSTEMS * o["max_extract_entries"], 0

    def count(n: int) -> None:
        nonlocal total
        total += n
        if total > cap:
            raise _bad(f"the {what} hold more than {cap} files and directories together "
                       f"({MAX_CHECKPOINTS_FILE_SYSTEMS} x MaxExtractEntries)")
    return count


def _read_topwater(src, o: dict) -> Recording:
    _check_entry_count(src, o)
    at = src.tell()
    with _open_zip(src) as zf:
        names = set(zf.namelist())
        if "history.jsonl" not in names:  # a snapshot: a recording without calls
            start = _read_manifest(zf, names, "manifest.json", o)[0]
            rec = Recording(start, {"user": start.user, "hostname": start.hostname, **o})
            rec.created, rec.topwater = start.created, None
            return rec
        with zf.open("history.jsonl") as f:
            lines = _lines(f, 2 * o["max_string_bytes"], o, "history.jsonl")
            h = _header(next(lines, (1, ""))[1], "topwater-history", HISTORY_VERSION, "history")
            calls = []
            for n, line in lines:
                c, e = _call(line, n, len(calls))
                calls.append((n, c, e.get("web")))  # only what is still needed: a capped history can hold 100000 calls
        web_bytes = 0

        def blob(name, size: int, where: str) -> bytes:
            nonlocal web_bytes
            web_bytes += size  # every response body together within one machine's worth of files
            if web_bytes > o["max_file_system_bytes"]:
                raise _bad(f"{where}: the recorded responses hold more than {o['max_file_system_bytes']} bytes (MaxFileSystemBytes)")
            if size == 0:
                return b""
            if not (isinstance(name, str) and _BLOB.fullmatch(name)):
                raise _bad(f"{where}: bad blob {json.dumps(name)}")
            if "blobs/" + name not in names:
                raise _bad(f"{where}: blob {name} is missing")
            with zf.open("blobs/" + name) as st:
                b = _read_capped(st, size, "blob " + name)
            if len(b) != size or hashlib.sha256(b).hexdigest() != name:
                raise _bad(f"{where}: blob {name} doesn't hold the {size} bytes it should")
            return b

        calls = [_with_web(c, web, n, blob) for n, c, web in calls]
        found = {}
        for name in names:
            if name.startswith("checkpoints/"):
                m = _CHECKPOINT.fullmatch(name)
                if m is None or int(m[1]) > len(calls):
                    raise _bad(f"bad checkpoint {json.dumps(name)}")
                found[int(m[1])] = name
        if 0 not in found:
            raise _bad("checkpoints/0.json is missing")
        if len(found) > MAX_CHECKPOINTS:
            raise _bad(f"more than {MAX_CHECKPOINTS} checkpoints")
        # the machine of the file is the one after the last checkpoint: a reader of either must see the same
        last_n = max(found)
        last = _manifest_bytes(zf, names, found[last_n], o)
        manifest = _manifest_bytes(zf, names, "manifest.json", o)
        if manifest != last:  # the same machine may still be written another way
            m = _manifest_json(manifest, "manifest.json")
            _check_header(m, "manifest.json")
            if m != _manifest_json(last, found[last_n]):
                raise _bad(f"manifest.json is not the machine of {found[last_n]}, the last checkpoint")
        total, cap = 0, MAX_CHECKPOINTS_FILE_SYSTEMS * o["max_file_system_bytes"]
        count_files = _file_budget(o, "checkpoints")

        def charge(size: int) -> None:
            nonlocal total
            total += size
            if total > cap:
                raise _bad(f"the checkpoints hold more than {cap} bytes of files together "
                           f"({MAX_CHECKPOINTS_FILE_SYSTEMS} x MaxFileSystemBytes)")

        snaps = {0: _read_manifest(zf, names, found[0], o, None, count_files)}
        charge(snaps[0][1])
        snaps[0] = snaps[0][0]
        # the other checkpoints are checked against the limits now and built when a replay needs one; the file stays in
        # memory for that, compressed, instead of every machine in it built
        if len(found) > 1:
            src.seek(at)
            data, kept, reread = src.read(), [], []
            for n in sorted(found)[1:]:
                def load(name=found[n]):
                    try:
                        if not reread:
                            reread.append(zipfile.ZipFile(io.BytesIO(data)))
                        return _read_manifest(reread[0], names, name, o)[0]
                    except (SnapshotError, SnapshotVersionError, MemoryError, KeyboardInterrupt):
                        raise
                    except Exception as e:  # anything else wrong with the bytes
                        raise RecordingError(str(e) or type(e).__name__) from e
                raw = last if n == last_n else _manifest_bytes(zf, names, found[n], o)
                snaps[n], size = _read_outline(raw, found[n], o, count_files, load, kept)
                charge(size)
        rec = _new_recording(h, snaps[0], o)
        for i, c in enumerate(calls):
            c.snapshot = snaps.get(i + 1)
        rec.calls = calls
        return rec


def _read_jsonl(src, max_bytes: int, o: dict, open_snapshot) -> Recording:
    """The JSON Lines recording Topwater wrote before the .topwater file: snapshots embedded as base64 zips or in side-car files."""
    lines = _lines(src, max_bytes, o, "the recording")
    h = _header(next(lines, (1, ""))[1], "topwater-recording", 1, "recording")
    calls = []
    for n, line in lines:
        calls.append(_call(line, n, len(calls)))
    if 1 + sum(e.get("snapshot") is not None for _, e in calls) > MAX_CHECKPOINTS:  # counted before any is read
        raise _bad(f"more than {MAX_CHECKPOINTS} snapshots")
    total, cap = 0, MAX_CHECKPOINTS_FILE_SYSTEMS * o["max_file_system_bytes"]
    count_files = _file_budget(o, "snapshots")

    def snap(ref, what: str) -> ShellSnapshot:
        nonlocal total
        if not isinstance(ref, dict):
            raise _bad(f"{what} has no snapshot")
        if isinstance(ref.get("embedded"), str):
            try:
                data = base64.b64decode(ref["embedded"], validate=True)
            except binascii.Error:
                raise _bad(f"{what}: the embedded snapshot is not base64") from None
        elif isinstance(ref.get("file"), str):
            rel = ref["file"]
            if open_snapshot is None:
                raise _bad(f"{what} is in a side-car file ({rel}); load the recording from its path")
            # a side-car name is only ever a plain relative name: nothing an opener could read as a drive, URL, escape or parent
            if not _SIDECAR.fullmatch(rel) or any(not s.strip(".") for s in rel.split("/")):
                raise _bad(f"snapshot path {json.dumps(rel)} is not a plain relative path inside the recording's directory")
            data = open_snapshot(rel)
        else:
            raise _bad(f"{what} has no snapshot")
        s, size = _read(data, o, count_files)
        total += size
        if total > cap:
            raise _bad(f"the snapshots hold more than {cap} bytes of files together ({MAX_CHECKPOINTS_FILE_SYSTEMS} x MaxFileSystemBytes)")
        return s

    rec = _new_recording(h, snap(h.get("start"), "the start"), o)
    for n, (c, e) in enumerate(calls):
        _with_web(c, e.get("web"), n + 2, None)
        c.snapshot = None if e.get("snapshot") is None else snap(e["snapshot"], f"call {c.index}")
        rec.calls.append(c)
    return rec
