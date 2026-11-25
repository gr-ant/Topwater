"""Recordings: a shell's calls with what they returned, saved in a .topwater file (spec/topwater-file) next to the machine.

A .topwater file is a zip: manifest.json (the machine after the last call), blobs/<sha256> shared by every manifest,
history.jsonl (a header, then one line per call) and checkpoints/<n>.json (the machine after n calls; 0 is the start).
Recording.load also reads a snapshot zip (a recording without calls) and the JSON Lines recordings Topwater wrote before.
"""
from __future__ import annotations

import base64
import binascii
import inspect
import json
import math
import os
import re
import zipfile
from dataclasses import dataclass, field
from datetime import datetime

from .shell import Shell, ShellResult
from .snapshot import (_HOST, _USER, ShellSnapshot, SnapshotError, SnapshotVersionError, _check_entry_count, _dumps,
                       _is_int, _max_archive, _max_manifest, _open, _read, _read_capped, _read_manifest, _put,
                       format_time, parse_time)
from .vfs import utc_now

HISTORY_VERSION = 1
MAX_CHECKPOINTS = 1000  # the start included
MAX_CHECKPOINTS_FILE_SYSTEMS = 4  # all checkpoints' files together: at most this many times max_file_system_bytes

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

    def create_shell(self, configure=None, start: ShellSnapshot | None = None) -> Shell:
        """A fresh shell with the recording's options, then configure(shell) (register your commands), then the start (or `start`)."""
        shell = Shell(**self.options)
        if configure is not None:
            configure(shell)
        shell.restore(start or self.start)
        return shell

    def shell_at(self, index: int, configure=None) -> Shell:
        """The machine after call `index` (-1: before the first): the newest checkpoint at or before it, then the calls after it run again."""
        if not -1 <= index < len(self.calls):
            raise IndexError(f"no call {index} in a recording of {len(self.calls)} calls")
        at = index
        while at >= 0 and self.calls[at].snapshot is None:
            at -= 1
        shell = self.create_shell(configure, self.calls[at].snapshot if at >= 0 else None)
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
        with zipfile.ZipFile(file, "w", zipfile.ZIP_DEFLATED, compresslevel=1) as zf:
            written: set[str] = set()
            checkpoints[-1][1]._write(zf, "manifest.json", written)
            zf.writestr("history.jsonl", b"".join(_dumps(line) + b"\n" for line in self._history()))
            for n, snap in checkpoints:
                snap._write(zf, f"checkpoints/{n}.json", written)

    def _history(self):
        o = self.options
        options = {"user": o["user"], "hostname": o["hostname"]}
        _put(options, startTime=self.start_time and format_time(self.start_time), randomSeed=self.random_seed)
        for k, name in _OPTIONS.items():
            options[name] = o[k] if k != "max_call_duration" else -1 if math.isinf(o[k]) else round(o[k] * 1000)
        header = {"format": "topwater-history", "version": HISTORY_VERSION}
        _put(header, created=self.created and format_time(self.created), topwater=self.topwater)
        header["options"] = options
        _put(header, appCommands=self.app_commands or None, metadata=self.metadata or None)
        yield header
        for c in self.calls:
            line = {"index": c.index, "command": c.command, "stdout": c.stdout, "stderr": c.stderr, "exitCode": c.exit_code}
            _put(line, trailer=c.trailer)
            line["elapsedMs"] = round(c.elapsed_ms, 3)
            _put(line, time=c.time and format_time(c.time), files=c.files or None)
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
                return _read_topwater(src, o) if zipped else _read_jsonl(src.read().decode("utf-8"), o, open_snapshot)
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
        self._last = shell.snapshot()
        self._marked = False  # snapshot_now() asked to keep the last call's checkpoint
        self.recording = Recording(self._last, {k: getattr(shell, k) for k in _DEFAULTS})
        self.recording.metadata = dict(metadata or {})
        builtins = shell._builtin_names or set()
        self.recording.app_commands = sorted(n for n in shell.commands if n not in builtins)

    def _add(self, command: str, result: ShellResult, elapsed_ms: float, started: datetime) -> None:
        calls = self.recording.calls
        if calls and not self._marked and not (self._every > 0 and len(calls) % self._every == 0):
            calls[-1].snapshot = None  # it was only kept as the end state, and the end moved on
        self._marked = False
        # a snapshot after every call: it is the end state, and comparing it with the one before gives the files changed
        snap = self._shell.snapshot()
        before = {e[0]: e[1:] for e in self._last.entries}
        after = {e[0]: e[1:] for e in snap.entries}
        files = sorted(p for p in before.keys() | after.keys() if before.get(p) != after.get(p))
        self._last = snap
        calls.append(RecordedCall(len(calls), command, result.stdout, result.stderr, result.exit_code, result.trailer,
                                  elapsed_ms, started, files, snap))

    def snapshot_now(self) -> None:
        """Keeps a checkpoint of the machine now: after the last call (or as the start, before any)."""
        self._last = self._shell.snapshot()
        if self.recording.calls:
            self.recording.calls[-1].snapshot = self._last
            self._marked = True
        else:
            self.recording.start = self._last

    def stop(self) -> None:
        """Stops recording; later calls are not added."""
        if self._shell._recorder is self:
            self._shell._recorder = None

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
    if seed is not None and not _is_int(seed):
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
    return rec


def _call(line: str, number: int, index: int) -> tuple[RecordedCall, dict]:
    try:
        e = json.loads(line)
    except ValueError:
        raise _bad(f"line {number} is not JSON") from None
    if not isinstance(e, dict):
        raise _bad(f"line {number} is not an object")
    if "index" in e and not _is_int(e["index"]):
        raise _bad(f"line {number}: bad index")
    index = e.get("index", index)  # checkpoints follow the line's position; the index is the file's own label
    command, stdout, stderr, trailer = e.get("command"), e.get("stdout", ""), e.get("stderr", ""), e.get("trailer")
    if not all(isinstance(s, str) for s in (command, stdout, stderr)) or not (trailer is None or isinstance(trailer, str)):
        raise _bad(f"line {number}: command, stdout, stderr and trailer must be strings")
    code, elapsed, files = e.get("exitCode", 0), e.get("elapsedMs", 0), e.get("files") or []
    if not _is_int(code):
        raise _bad(f"line {number}: bad exitCode")
    if isinstance(elapsed, bool) or not isinstance(elapsed, (int, float)) or not elapsed >= 0:
        raise _bad(f"line {number}: bad elapsedMs")
    if not isinstance(files, list) or not all(isinstance(f, str) for f in files):
        raise _bad(f"line {number}: files is not a list of paths")
    t = None if e.get("time") is None else parse_time(e["time"], f"line {number}: time")
    return RecordedCall(index, command, stdout, stderr, code, trailer, float(elapsed), t, list(files)), e


def _read_topwater(src, o: dict) -> Recording:
    _check_entry_count(src, o)
    with zipfile.ZipFile(src) as zf:
        names = set(zf.namelist())
        if "history.jsonl" not in names:  # a snapshot: a recording without calls
            start = _read_manifest(zf, names, "manifest.json", o)[0]
            rec = Recording(start, {"user": start.user, "hostname": start.hostname, **o})
            rec.created, rec.topwater = start.created, None
            return rec
        with zf.open("history.jsonl") as f:
            lines = _read_capped(f, _max_manifest(o), "history.jsonl").decode("utf-8").split("\n")
        h = _header(lines[0], "topwater-history", HISTORY_VERSION, "history")
        calls = [_call(line, n + 2, n)[0] for n, line in enumerate(x for x in lines[1:] if x)]
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
        total, cap, cache = 0, MAX_CHECKPOINTS_FILE_SYSTEMS * o["max_file_system_bytes"], {}
        snaps = {}
        for n in sorted(found):
            snaps[n], size = _read_manifest(zf, names, found[n], o, cache)
            total += size
            if total > cap:
                raise _bad(f"the checkpoints hold more than {cap} bytes of files together "
                           f"({MAX_CHECKPOINTS_FILE_SYSTEMS} x MaxFileSystemBytes)")
        rec = _new_recording(h, snaps[0], o)
        for i, c in enumerate(calls):
            c.snapshot = snaps.get(i + 1)
        rec.calls = calls
        return rec


def _read_jsonl(text: str, o: dict, open_snapshot) -> Recording:
    """The JSON Lines recording Topwater wrote before the .topwater file: snapshots embedded as base64 zips or in side-car files."""
    lines = text.split("\n")
    h = _header(lines[0], "topwater-recording", 1, "recording")
    calls = [_call(line, n, i) for i, (n, line) in enumerate((n, x) for n, x in enumerate(lines[1:], 2) if x)]
    if 1 + sum(e.get("snapshot") is not None for _, e in calls) > MAX_CHECKPOINTS:  # counted before any is read
        raise _bad(f"more than {MAX_CHECKPOINTS} snapshots")
    total, cap = 0, MAX_CHECKPOINTS_FILE_SYSTEMS * o["max_file_system_bytes"]

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
        s, size = _read(data, o)
        total += size
        if total > cap:
            raise _bad(f"the snapshots hold more than {cap} bytes of files together ({MAX_CHECKPOINTS_FILE_SYSTEMS} x MaxFileSystemBytes)")
        return s

    rec = _new_recording(h, snap(h.get("start"), "the start"), o)
    for c, e in calls:
        c.snapshot = None if e.get("snapshot") is None else snap(e["snapshot"], f"call {c.index}")
        rec.calls.append(c)
    return rec
