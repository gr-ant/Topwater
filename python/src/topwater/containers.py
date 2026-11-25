"""Moves a machine between Topwater and a real Docker container, both ways ("Containers" in docs/reference.md).

Host APIs only: nothing here registers a command, so the agent inside the shell can never trigger an export, a build or
an import. Only `_docker` starts processes: the docker CLI, with an argument list and no shell. Everything an import
builds goes back through ShellSnapshot.load and its checks for untrusted input. Standard library only.
"""
from __future__ import annotations

import calendar
import hashlib
import io
import json
import os
import re
import subprocess
import tarfile
import tempfile
import unicodedata
import zipfile
from dataclasses import dataclass, field, replace
from datetime import datetime, timezone

from .shell import Shell
from .snapshot import ShellSnapshot, format_time, parse_time
from .vfs import get_parent, normalize, utc_now

DEFAULT_BASE_IMAGE = "ubuntu:24.04"
EXECUTABLE = "docker"  # the docker CLI to run; something CLI-compatible (podman) may work but is untested

SESSION_PATH, PATHS_PATH = "/etc/topwater/session.sh", "/etc/topwater/paths"
# appended to the user's ~/.bashrc; an import strips exactly this
BASHRC_HOOK = ("\n# Topwater session: variables, functions, aliases and options from the snapshot\n"
               "[ -r /etc/topwater/session.sh ] && . /etc/topwater/session.sh\n")

SIMULATED = ("/proc", "/sys", "/dev", "/run")  # simulated by Topwater; a container has the real ones
ACCOUNT_FILES = {"/etc/passwd", "/etc/group", "/etc/shadow", "/etc/gshadow"}  # accounts are made with useradd/groupadd instead
DOCKER_MANAGED = {"/etc/hostname", "/etc/hosts", "/etc/resolv.conf"}  # docker writes these when a container starts
STUB = b"\x7fELF\x02\x01\x01\x00\x00\x00\x00\x00\x00\x00\x00\x00"  # the program stub Topwater writes for each command
# bash sets these itself, or they describe the running process: never carried
MANAGED_VARIABLES = {
    "PWD", "OLDPWD", "SHLVL", "_", "BASHPID", "PPID", "UID", "EUID", "GROUPS", "SHELLOPTS", "BASHOPTS", "RANDOM", "SRANDOM", "SECONDS",
    "LINENO", "FUNCNAME", "PIPESTATUS", "HISTCMD", "EPOCHSECONDS", "EPOCHREALTIME", "DIRSTACK", "COLUMNS", "LINES", "HOSTNAME",
    "BASH", "BASH_VERSION", "BASH_VERSINFO", "BASH_SOURCE", "BASH_LINENO", "BASH_ARGC", "BASH_ARGV", "BASH_COMMAND", "BASH_SUBSHELL",
    "OPTIND", "OPTARG",
}
_USER_NAME = re.compile(r"[A-Za-z0-9_][A-Za-z0-9_.-]{0,31}")
_VAR_NAME = re.compile(r"[A-Za-z_][A-Za-z0-9_]*")


class ContainerError(Exception):
    """A docker command failed."""


class InvalidContainerData(ValueError):
    """A container's filesystem archive is malformed or over a limit."""


@dataclass(frozen=True)
class ReportItem:
    what: str
    why: str

    def __str__(self) -> str:
        return f"{self.what}: {self.why}"


@dataclass
class ContainerReport:
    """What an export or import carried over and what it couldn't."""
    carried: list[str] = field(default_factory=list)  # paths written into the image's layer, or into the Topwater machine
    deleted: list[str] = field(default_factory=list)  # paths removed because the source machine had deleted them
    not_carried: list[ReportItem] = field(default_factory=list)  # everything left behind, each with the reason

    def skip(self, what: str, why: str) -> None:
        self.not_carried.append(ReportItem(what, why))

    def __str__(self) -> str:
        return (f"{len(self.carried)} paths carried, {len(self.deleted)} deleted, {len(self.not_carried)} not carried"
                + "".join("\n  " + str(i) for i in self.not_carried))


def under(path: str, d: str) -> bool:
    return path == d or path.startswith(d + "/")


def usr_merge(path: str) -> str:
    """Ubuntu merged /bin, /sbin and /lib into /usr: they are symlinks there, which a layer must not replace with directories."""
    return "/usr" + path if any(under(path, d) for d in ("/bin", "/sbin", "/lib", "/lib64", "/lib32", "/libx32")) else path


def quote(s: str) -> str:
    """Quotes s as one bash word."""
    if s and all((c.isascii() and c.isalnum()) or c in "_./:@%+=,-" for c in s):
        return s
    if not any(_control(c) for c in s):
        return "'" + s.replace("'", "'\\''") + "'"
    esc = {"\\": "\\\\", "'": "\\'", "\n": "\\n", "\t": "\\t", "\r": "\\r"}
    return "$'" + "".join(esc.get(c) or (f"\\u{ord(c):04x}" if _control(c) else c) for c in s) + "'"


def _control(c: str) -> bool:
    return unicodedata.category(c) == "Cc"


def _is_plain_name(s: str) -> bool:
    return bool(s) and all((c.isascii() and c.isalnum()) or c in "_-." for c in s)


# ---------- a snapshot opened through its saved format ----------

@dataclass(frozen=True)
class _Item:
    """One file, directory or symbolic link (link_target) of a snapshot; data is None for a directory. hard_link: the path of
    the same file's earlier name, when it has several."""
    path: str
    is_dir: bool
    mode: int
    owner: str
    mtime: datetime
    data: bytes | None = None
    binary: bool = False
    reported_size: int | None = None
    link_target: str | None = None
    hard_link: str | None = None

    def same_content(self, other: "_Item") -> bool:
        return ((self.is_dir, self.mode, self.owner, self.link_target, self.hard_link) == (other.is_dir, other.mode, other.owner, other.link_target, other.hard_link)
                and (self.is_dir or self.data == other.data))


class _Machine:
    """A snapshot read through its saved format (manifest.json + blobs): the versioned format is the contract, and all this
    module builds goes back through ShellSnapshot.load and its checks."""

    def __init__(self, manifest: dict, files: dict[str, _Item]):
        self.manifest, self.files = manifest, files

    @staticmethod
    def read(snapshot: ShellSnapshot) -> "_Machine":
        buf = io.BytesIO()
        snapshot.save(buf)
        with zipfile.ZipFile(buf) as zf:
            manifest = json.loads(zf.read("manifest.json"))
            files = {}
            for f in manifest.pop("files"):
                data = None if f["type"] == "dir" else zf.read("blobs/" + f["blob"]) if f.get("blob") else b""
                files[f["path"]] = _Item(f["path"], f["type"] == "dir", int(f["mode"], 8), f["owner"], parse_time(f["mtime"], "mtime"),
                                         data, f.get("binary", False), f.get("reportedSize"), f.get("target"), f.get("link"))
        return _Machine(manifest, dict(sorted(files.items())))

    @staticmethod
    def default(user: str, hostname: str) -> "_Machine":
        """A fresh machine for user: what a new Shell holds before the agent does anything."""
        return _Machine.read(Shell(user, hostname).snapshot())

    def to_snapshot(self, limits) -> ShellSnapshot:
        """Writes this as a saved snapshot and loads it back with `limits`: the format checks and limits apply."""
        buf = io.BytesIO()
        with zipfile.ZipFile(buf, "w", zipfile.ZIP_DEFLATED, compresslevel=1) as zf:
            written, files, links = set(), [], False
            for f in sorted(self.files.values(), key=lambda i: i.path):
                entry = {"path": f.path, "type": "dir" if f.is_dir else "symlink" if f.link_target is not None else "file", "mode": f"{f.mode:04o}",
                         "owner": f.owner, "mtime": format_time(f.mtime)}
                if f.link_target is not None:
                    entry["target"] = f.link_target
                first = self.files.get(f.hard_link) if f.hard_link else None
                if first is not None and not first.is_dir and first.hard_link is None and first.path < f.path:
                    entry["link"] = f.hard_link  # else its first name is gone or comes later: a file of its own
                links = links or "target" in entry or "link" in entry
                if not f.is_dir and f.link_target is None:
                    entry["size"] = len(f.data)
                    if f.binary:
                        entry["binary"] = True
                    if f.reported_size is not None:
                        entry["reportedSize"] = f.reported_size
                    if f.data:
                        blob = entry["blob"] = hashlib.sha256(f.data).hexdigest()
                        if blob not in written:
                            written.add(blob)
                            zf.writestr("blobs/" + blob, f.data)
                files.append(entry)
            zf.writestr("manifest.json", json.dumps({**self.manifest, **({"version": 2} if links else {}), "files": files}))  # links need version 2
        return ShellSnapshot.load(buf.getvalue(), limits)

    @property
    def variables(self) -> list[dict]:
        return self.manifest.get("variables") or []

    def set_exported(self, name: str, value: str) -> None:
        """Sets an exported scalar variable, replacing whatever had the name."""
        self.manifest["variables"] = [v for v in self.variables if v.get("name") != name] + [{"name": name, "value": value, "exported": True}]

    def text(self, path: str) -> str | None:
        f = self.files.get(path)
        return None if f is None or f.is_dir else f.data.decode("utf-8", "replace")

    def strings(self, key: str) -> list[str]:
        return list(self.manifest.get(key) or [])

    def remove_tree(self, path: str) -> None:
        for p in [p for p in self.files if under(p, path)]:
            del self.files[p]


@dataclass(frozen=True)
class _Account:
    name: str
    uid: int
    gid: int
    gecos: str
    home: str
    shell: str


def _parse_passwd(text: str | None) -> list[_Account]:
    accounts = []
    for line in (text or "").split("\n"):
        p = line.split(":")
        if len(p) == 7 and p[0] and p[2].isascii() and p[2].isdigit() and p[3].isascii() and p[3].isdigit():
            accounts.append(_Account(p[0], int(p[2]), int(p[3]), p[4], p[5], p[6]))
    return accounts


def _parse_group(text: str | None) -> list[tuple[str, int]]:
    groups = []
    for line in (text or "").split("\n"):
        p = line.split(":")
        if len(p) >= 3 and p[0] and p[2].isascii() and p[2].isdigit():
            groups.append((p[0], int(p[2])))
    return groups


def _is_stub(f: _Item) -> bool:
    return not f.is_dir and f.data == STUB


def _system_needs(path: str, f: _Item) -> bool:
    return any(under(path, s) for s in SIMULATED) or _is_stub(f) or path in ACCOUNT_FILES or path in DOCKER_MANAGED


# ---------- export: snapshot to container ----------

@dataclass(frozen=True)
class BuildContext:
    """A Docker build context for a snapshot. Nothing here starts a process; build_image hands it to docker."""
    dockerfile: str
    setup_script: str  # setup.sh, run as root while the image builds: accounts, deleted files, the ~/.bashrc hook
    session_script: str  # the shell state that isn't files, as bash; it lands at /etc/topwater/session.sh
    layer: bytes  # layer.tar: the files, with numeric owners, modes and modification times
    report: ContainerReport

    def write_to(self, directory) -> None:
        """Writes Dockerfile, setup.sh and layer.tar into directory (created if missing)."""
        os.makedirs(directory, exist_ok=True)
        for name, data in self._files():
            with open(os.path.join(directory, name), "wb") as f:
                f.write(data)

    def write_tar(self, fileobj) -> None:
        """Writes the same three files as one tar, the form `docker build -` reads from stdin."""
        # ustar: docker detects a context tar by its first header, so no PAX extended header
        with tarfile.open(fileobj=fileobj, mode="w", format=tarfile.USTAR_FORMAT) as tar:
            for name, data in self._files():
                info = tarfile.TarInfo(name)
                info.size, info.mode = len(data), 0o644
                tar.addfile(info, io.BytesIO(data))

    def _files(self):
        return [("Dockerfile", self.dockerfile.encode()), ("setup.sh", self.setup_script.encode()), ("layer.tar", self.layer)]


def to_build_context(snapshot: ShellSnapshot, base_image: str = DEFAULT_BASE_IMAGE) -> BuildContext:
    """The Docker build context for snapshot. Only what differs from a fresh Topwater machine is exported; base_image
    (Ubuntu 24.04, which Topwater imitates) provides the real system. Pure: no process, no disk."""
    if "\n" in base_image or "\r" in base_image or not base_image.strip():
        raise ValueError("base_image must be one image reference on one line")
    return _Exporter(snapshot, base_image).build()


def build_image(snapshot: ShellSnapshot, tag: str, base_image: str = DEFAULT_BASE_IMAGE) -> BuildContext:
    """Builds an image tagged `tag` with `docker build` from to_build_context, and returns the context, whose report says
    what didn't carry over. `docker run -it TAG` then lands in the machine."""
    context = to_build_context(snapshot, base_image)
    buf = io.BytesIO()
    context.write_tar(buf)
    _docker(["build", "-q", "-t", tag, "-"], stdin=buf.getvalue())
    return context


class _Exporter:
    def __init__(self, snapshot: ShellSnapshot, base_image: str):
        self.snapshot, self.base_image = snapshot, base_image
        self.report = ContainerReport()
        self.snap = _Machine.read(snapshot)
        self.default = _Machine.default(snapshot.user, snapshot.hostname)

    def changed(self, path: str) -> bool:
        f, d = self.snap.files.get(path), self.default.files.get(path)
        return f is not None and not (d is not None and f.same_content(d))

    def build(self) -> BuildContext:
        snap, default, report, user = self.snap, self.default, self.report, self.snapshot.user
        accounts = _parse_passwd(snap.text("/etc/passwd"))
        groups = _parse_group(snap.text("/etc/group"))
        home = next((a.home for a in accounts if a.name == user), "/home/" + user)

        # ---- files: what differs from a fresh machine, by path in the image ----
        layer: dict[str, _Item] = {}
        for f in snap.files.values():
            if f.path == "/" or not self.changed(f.path):
                continue
            target = usr_merge(f.path)
            if any(under(f.path, s) for s in SIMULATED):
                report.skip(f.path, "simulated by Topwater; the container has its own")
            elif _is_stub(f):
                if f.path not in default.files:
                    report.skip(f.path, "a Topwater program stub (16 bytes standing in for a program), not a real program")
            elif f.path in ACCOUNT_FILES:
                report.skip(f.path, "not copied: its accounts are created with useradd/groupadd instead")
            elif f.path in DOCKER_MANAGED:
                report.skip(f.path, "docker writes this file when the container starts (see docker run --hostname, --add-host, --dns)")
            elif target != f.path and self.changed(target):
                report.skip(f.path, f"is {target} in the image, which the snapshot also changed; kept {target}")
            else:
                layer[target] = replace(f, path=target)
        # parents too, so each lands with the snapshot's owner and mode rather than root 0755
        for path in sorted(layer):
            p = get_parent(path)
            while p != "/" and p not in layer:
                d = snap.files.get(p)
                if d is not None and d.is_dir:
                    layer[p] = d
                p = get_parent(p)
        layer = dict(sorted(layer.items()))
        report.carried.extend(layer)

        # ---- deleted: in a fresh machine, gone from the snapshot ----
        deleted: list[str] = []
        for d in default.files.values():
            if d.path in snap.files or any(under(d.path, x) for x in deleted):
                continue
            if _system_needs(d.path, d):
                report.skip(d.path, "deleted in the snapshot, but kept: the real system needs it")
            else:
                deleted.append(usr_merge(d.path))
        report.deleted.extend(deleted)

        # ---- shell state: ENV, the session script, history, the ~/.bashrc hook ----
        session, env = self.session()
        now = utc_now()
        bashrc = home + "/.bashrc"
        rc = layer.get(bashrc)
        hook_in_layer = rc is not None and not rc.is_dir
        if hook_in_layer:
            layer[bashrc] = replace(rc, data=rc.data + BASHRC_HOOK.encode())
        paths = "".join(f"A {p}\n" for p in layer) + "".join(f"D {p}\n" for p in deleted)
        layer.setdefault("/etc/topwater", _Item("/etc/topwater", True, 0o755, "root", now))
        layer[SESSION_PATH] = _Item(SESSION_PATH, False, 0o644, "root", now, session.encode())
        layer[PATHS_PATH] = _Item(PATHS_PATH, False, 0o644, "root", now, paths.encode())
        history = snap.strings("history")
        if history:
            # timestamped, so a multi-line command stays one entry
            stamp = calendar.timegm(self.snapshot.created.utctimetuple())
            layer[home + "/.bash_history"] = _Item(home + "/.bash_history", False, 0o600, user, self.snapshot.created,
                                                   "".join(f"#{stamp}\n{h}\n" for h in history).encode())

        # ---- accounts: every owner in the layer needs a real uid, known before the build ----
        owners = sorted({f.owner for f in layer.values()} | {user})
        ids = [i for i in [a.uid for a in accounts] + [g for _, g in groups] if 1000 <= i < 60000]
        nxt = max(ids) + 1 if ids else 1000
        for o in owners:
            if not any(a.name == o for a in accounts):
                accounts.append(_Account(o, nxt, nxt, "", "/home/" + o, "/bin/bash"))
                report.skip("owner " + o, f"not in the snapshot's /etc/passwd; created as uid {nxt}")
                nxt += 1
        by_name: dict[str, _Account] = {}
        for a in accounts:
            by_name.setdefault(a.name, a)
        needed = list(by_name.values())  # every account in the snapshot, plus the owners added above

        self.not_carried_state()
        dockerfile = self.dockerfile(env, user)
        setup = _setup(accounts, groups, needed, deleted, None if hook_in_layer else bashrc, user)
        return BuildContext(dockerfile, setup, session, _layer(layer, by_name), report)

    def session(self) -> tuple[str, list[tuple[str, str]]]:
        """The session script, and the exported scalars a Dockerfile ENV can hold instead."""
        env: list[tuple[str, str]] = []
        out = [f"# Generated by Topwater from a snapshot taken {self.snapshot.created:%Y-%m-%d %H:%M:%S}Z.\n",
               "# Sourced by ~/.bashrc, so an interactive shell starts where the Topwater session was.\n\n"]
        # variables that differ from a fresh machine's
        defaults = {v["name"]: v for v in self.default.variables}
        names = set()
        for v in self.snap.variables:
            name = v["name"]
            names.add(name)
            if name in MANAGED_VARIABLES or defaults.get(name) == v:
                continue
            value, attrs, exported, array = v.get("value"), v.get("attrs") or "", bool(v.get("exported")), v.get("array")
            if exported and array is None and value is not None and not any(_control(c) for c in value) and not attrs.strip("x"):
                env.append((name, value))
                continue
            flags = "".join(dict.fromkeys(c for c in attrs if c in "irlunx")) + ("x" if exported and "x" not in attrs else "")
            if array is not None:
                assoc = bool(array.get("assoc"))
                items = " ".join(f"[{quote(k) if assoc else k}]={quote(x)}" for k, x in array.get("items") or [])
                out.append(f"declare -{flags}{'A' if assoc else 'a'} {name}=({items})\n")
            else:
                out.append((f"declare -{flags} " if flags else "") + (name if value is None else f"{name}={quote(value)}") + "\n")
        out.extend(f"unset {d}\n" for d in defaults if d not in names and d not in MANAGED_VARIABLES)

        for f in self.snap.manifest.get("functions") or []:
            out.append(f"{f['name']}() {f['body']}\n")
            if f.get("exported"):
                out.append(f"export -f {f['name']}\n")

        out.append("unalias -a\n")  # ~/.bashrc's own aliases give way to the snapshot's
        out.extend(f"alias {quote(k + '=' + v)}\n" for k, v in (self.snap.manifest.get("aliases") or {}).items())

        for o in self.snap.strings("set"):
            if o == "errexit":
                self.report.skip("set -o errexit", "left off: in an interactive shell it closes the terminal at the first failing command")
            else:
                out.append(f"set -o {o}\n")
        shopt, default_shopt = self.snap.strings("shopt"), self.default.strings("shopt")
        out.extend(f"shopt -s {o} 2>/dev/null\n" for o in dict.fromkeys(shopt) if o not in default_shopt)
        out.extend(f"shopt -u {o} 2>/dev/null\n" for o in dict.fromkeys(default_shopt) if o not in shopt)
        out.extend(f"trap -- {quote(action)} {sig}\n" for sig, action in (self.snap.manifest.get("traps") or {}).items())
        positional = self.snap.strings("positional")
        if positional:
            out.append(f"set -- {' '.join(quote(p) for p in positional)}\n")
        return "".join(out), env

    def not_carried_state(self) -> None:
        r, snap = self.report, self.snap
        commands = [f.path[9:] for f in snap.files.values() if f.path.startswith("/usr/bin/") and _is_stub(f) and f.path not in self.default.files]
        if commands:
            r.skip("registered commands", ", ".join(commands) + ": host-side handlers, not programs")
        if snap.strings("dirStack"):
            r.skip("directory stack (pushd)", "not recreated; the shell starts in the working directory")
        if snap.manifest.get("jobs"):
            r.skip("finished background jobs", "process state; a new shell has none")
        r.skip("$?, PIPESTATUS, $_, $!, SECONDS, getopts position", "per-process; a new shell starts them fresh")
        r.skip(f"hostname {self.snapshot.hostname}", "an image can't set it; pass --hostname to docker run")
        r.skip("Topwater's limits (max_file_system_bytes, max_call_duration, ...)", "a real container isn't bound by them; use docker run's limits")

    def dockerfile(self, env: list[tuple[str, str]], user: str) -> str:
        def env_value(s: str) -> str:
            return '"' + s.replace("\\", "\\\\").replace('"', '\\"').replace("$", "\\$") + '"'

        # a line break ends a Dockerfile instruction and quoting can't carry one, so such values never reach the file
        def one_line(s: str) -> bool:
            return "\n" not in s and "\r" not in s

        def flat(s: str) -> str:
            return s.replace("\n", " ").replace("\r", " ")

        if not _is_plain_name(user):
            raise ValueError(f"snapshot user {flat(user)} is not a plain account name")
        out = [f"# Generated by Topwater from a snapshot of {flat(self.snapshot.user)}@{flat(self.snapshot.hostname)} "
               f"taken {self.snapshot.created:%Y-%m-%d %H:%M:%S}Z.\n",
               f"FROM {self.base_image}\n", "COPY setup.sh /tmp/topwater-setup.sh\n", "RUN bash /tmp/topwater-setup.sh && rm /tmp/topwater-setup.sh\n",
               "ADD layer.tar /\n"]
        for n, v in env:
            if not _is_plain_name(n) or "-" in n or not one_line(v):
                self.report.skip(f"environment variable {flat(n)}", "its name or value can't be written safely into a Dockerfile (e.g. a line break)")
                continue
            out.append(f"ENV {n}={env_value(v)}\n")
        cwd = self.snap.manifest["cwd"]
        if one_line(cwd):
            out.append(f"WORKDIR {env_value(cwd)}\n")
        else:
            self.report.skip("working directory", "its path contains a line break, which a Dockerfile can't hold; the container starts in /")
        out.append(f"USER {user}\n")
        return "".join(out)


def _setup(accounts: list[_Account], groups: list[tuple[str, int]], needed: list[_Account], deleted: list[str], hook_bashrc: str | None, user: str) -> str:
    out = ["#!/bin/bash\n# Generated by Topwater: accounts, deletions and the session hook. Runs once, as root, while the image builds.\nset -e\n"]
    # an account of the base image holding an id the snapshot needs gives way (ubuntu:24.04 ships "ubuntu", uid and gid 1000)
    keep = "|".join(dict.fromkeys([a.name for a in accounts] + [g for g, _ in groups]))
    out.append(f"keep={quote('|' + keep + '|')}\n")
    out.append('free_uid() { o=$(getent passwd "$1" | cut -d: -f1); case "$keep" in *"|$o|"*) ;; *) [ -z "$o" ] || userdel -r "$o" 2>/dev/null || userdel "$o";; esac; }\n')
    out.append('free_gid() { o=$(getent group "$1" | cut -d: -f1); case "$keep" in *"|$o|"*) ;; *) [ -z "$o" ] || groupdel "$o";; esac; }\n')
    out.extend(f"free_uid {a.uid}\n" for a in needed if a.uid >= 1000)
    for gid in dict.fromkeys(a.gid for a in needed):
        name = next((g for g, i in groups if i == gid), None) or next(a.name for a in needed if a.gid == gid)
        if gid >= 1000:
            out.append(f"free_gid {gid}\n")
        out.append(f"getent group {quote(name)} >/dev/null || groupadd -o -g {gid} {quote(name)}\n")
    for a in needed:
        out.append(f"id -u {quote(a.name)} >/dev/null 2>&1 || useradd -o {'-m' if a.home.startswith('/home/') else '-M'} -u {a.uid} -g {a.gid} "
                   f"-d {quote(a.home)} -s {quote(a.shell or '/bin/bash')} -c {quote(a.gecos)} {quote(a.name)}\n")
    if deleted:
        out.append(f"rm -rf -- {' '.join(quote(d) for d in deleted)}\n")
    if hook_bashrc is not None:
        out.append(f"if [ -d {quote(get_parent(hook_bashrc))} ]; then printf '%s' {quote(BASHRC_HOOK)} >> {quote(hook_bashrc)}; "
                   f"chown {quote(user)}: {quote(hook_bashrc)}; fi\n")
    return "".join(out)


def _layer(layer: dict[str, _Item], accounts: dict[str, _Account]) -> bytes:
    buf = io.BytesIO()
    with tarfile.open(fileobj=buf, mode="w", format=tarfile.GNU_FORMAT) as tar:
        for path, f in layer.items():
            a = accounts.get(f.owner) or _Account("root", 0, 0, "", "/root", "")
            info = tarfile.TarInfo(path[1:] + ("/" if f.is_dir else ""))
            # another name of a file goes in as a hard link when its first name is earlier in the layer, else as a copy
            hard = usr_merge(f.hard_link) if f.hard_link and usr_merge(f.hard_link) in layer and usr_merge(f.hard_link) < path else None
            info.type = (tarfile.DIRTYPE if f.is_dir else tarfile.LNKTYPE if hard else tarfile.SYMTYPE if f.link_target is not None else tarfile.REGTYPE)
            info.mode, info.uid, info.gid, info.uname = f.mode & 0o7777, a.uid, a.gid, a.name
            info.mtime = calendar.timegm(f.mtime.utctimetuple())
            if hard:
                info.linkname = hard[1:]
            elif f.link_target is not None:
                info.linkname = f.link_target
            regular = info.type == tarfile.REGTYPE
            info.size = len(f.data) if regular else 0
            tar.addfile(info, io.BytesIO(f.data) if regular else None)
    return buf.getvalue()


# ---------- import: container to snapshot ----------

@dataclass(frozen=True)
class ContainerConfig:
    """What `docker inspect` says: Config.User, Config.WorkingDir, Config.Env ("NAME=value")."""
    user: str | None = None
    working_dir: str | None = None
    env: tuple[str, ...] = ()


@dataclass(frozen=True)
class ImportOptions:
    paths: tuple[str, ...] = ()  # imported whole (with everything below) on top of what the container changed, e.g. /etc, /var/log
    include_binaries: bool = False  # import ELF executables as inert files; off, they're skipped and reported
    restore_session: bool = True  # for an image Topwater exported, re-create the session by running session.sh in the simulated shell
    limits: dict = field(default_factory=dict)  # Shell() arguments: max_file_system_bytes, max_string_bytes, max_extract_entries, hostname, ...


@dataclass(frozen=True)
class ImportResult:
    snapshot: ShellSnapshot
    report: ContainerReport


def from_container(container: str, options: ImportOptions | None = None) -> ImportResult:
    """Imports a container (running or stopped): what it changed relative to its image (`docker diff`), plus options.paths,
    plus, for an image Topwater exported, the files that export put there. They are layered onto a fresh Topwater machine,
    so the simulated programs still work. User, environment and working directory come from `docker inspect`.
    Synchronous; it runs the session script with Shell.run, so call it outside a running event loop."""
    options = options or ImportOptions()
    c = json.loads(_docker(["container", "inspect", container]))[0].get("Config") or {}

    def s(name):
        v = c.get(name)
        return v if isinstance(v, str) else None

    env = tuple(x if isinstance(x, str) else "" for x in c.get("Env") or [])
    changes = _docker(["container", "diff", container]).split("\n") + _topwater_paths(container, _limit(options, "max_string_bytes"))
    importer = _Importer(ContainerConfig(s("User"), s("WorkingDir"), env), changes, options)
    _docker(["container", "export", container], read=importer.read)
    return importer.finish()


def from_image(image: str, options: ImportOptions | None = None) -> ImportResult:
    """Imports an image: a container is created from it (never started), imported with from_container, and removed."""
    cid = _docker(["create", image, "true"]).strip()
    try:
        return from_container(cid, options)
    finally:
        try:
            _docker(["rm", "-f", cid])
        except ContainerError:
            pass  # don't hide the import's own error


def from_tar(rootfs, config: ContainerConfig, changes=(), options: ImportOptions | None = None) -> ImportResult:
    """The process-free core of an import: a root filesystem tar (what `docker export` writes) as a binary file or bytes,
    the container's config, and its changes as `docker diff` prints them ("A /path", "C /path", "D /path"). Only changed
    paths and options.paths are imported. Raises InvalidContainerData for a malformed archive or one over a limit."""
    if isinstance(rootfs, (bytes, bytearray)):
        rootfs = io.BytesIO(rootfs)
    importer = _Importer(config, changes, options or ImportOptions())
    importer.read(rootfs)
    return importer.finish()


def _limit(options: ImportOptions, name: str) -> int:
    defaults = {"max_file_system_bytes": 128 << 20, "max_string_bytes": 32 << 20, "max_extract_entries": 100_000}
    return options.limits.get(name, defaults[name])


def _topwater_paths(container: str, max_bytes: int) -> list[str]:
    """The paths a Topwater export wrote or deleted, in docker diff's format; empty for other images."""
    lines: list[str] = []

    def read(stream):
        with tarfile.open(fileobj=stream, mode="r|") as tar:
            for m in tar:
                if m.isreg() and m.size <= max_bytes:
                    lines.extend(tar.extractfile(m).read().decode("utf-8", "replace").split("\n"))
                break
        while stream.read(1 << 16):
            pass

    try:
        _docker(["container", "cp", f"{container}:{PATHS_PATH}", "-"], read=read)
    except (ContainerError, tarfile.TarError):
        pass  # not a Topwater image
    return lines


def _canonical(name: str) -> str:
    if name in (".", "./"):
        return "/"
    n = name[2:] if name.startswith("./") else name
    if n.endswith("/"):
        n = n[:-1]
    path = "/" + n
    if len(path) > 4096 or "\0" in path or normalize("/", path) != path:
        raise InvalidContainerData(f"not a canonical path in the container's filesystem: {json.dumps(name)}")
    return path


def _captured(path: str) -> bool:
    return path in ("/etc/passwd", SESSION_PATH) or (path.endswith("/.bash_history")
                                                     and (path == "/root/.bash_history" or get_parent(get_parent(path)) == "/home"))


@dataclass
class _Entry:
    is_dir: bool
    mode: int
    uid: int
    mtime: datetime
    data: bytes | None
    link_target: str | None = None  # a symbolic link: the path it holds


class _Importer:
    def __init__(self, config: ContainerConfig, changes, options: ImportOptions):
        self.config, self.o = config, options
        self.max_fs, self.max_str, self.max_entries = (_limit(options, n) for n in ("max_file_system_bytes", "max_string_bytes", "max_extract_entries"))
        self.report = ContainerReport()
        self.changed: set[str] = set()
        self.deleted: list[str] = []
        self.prefixes = [normalize("/", p) for p in options.paths]
        self.items: dict[str, _Entry] = {}
        self.links: dict[str, str] = {}
        self.captured: dict[str, bytes] = {}  # /etc/passwd, the session script, histories
        self.bytes = self.count = 0
        for line in changes:
            if len(line) < 3 or line[1] != " " or line[2] != "/":
                continue
            path = line[2:].rstrip("\r")
            (self.deleted.append if line[0] == "D" else self.changed.add)(path)

    def selected(self, path: str) -> bool:
        return path in self.changed or any(p == "/" or under(path, p) for p in self.prefixes)

    def read(self, stream) -> None:
        try:
            with tarfile.open(fileobj=stream, mode="r|") as tar:
                for m in tar:
                    self.entry(tar, m)
            while stream.read(1 << 16):  # the end-of-archive padding, so docker isn't left blocked writing it
                pass
        except (InvalidContainerData, ContainerError):
            raise
        except (tarfile.TarError, EOFError, OSError, ValueError, UnicodeError) as e:
            raise InvalidContainerData(f"not a valid container filesystem archive: {e}") from e

    def entry(self, tar: tarfile.TarFile, e: tarfile.TarInfo) -> None:
        path = _canonical(e.name)
        data = None
        if e.isreg() and _captured(path) and e.size <= self.max_str:
            data = self.captured[path] = self._data(tar, e)
        if path == "/" or not self.selected(path):
            return
        self.count += 1
        if self.count > self.max_entries:
            raise InvalidContainerData(f"more than {self.max_entries} entries to import (max_extract_entries)")
        if under(path, "/etc/topwater"):
            return  # a Topwater export's own files
        if any(under(path, s) for s in SIMULATED) or path in DOCKER_MANAGED:
            self.report.skip(path, "Topwater simulates it, or docker writes it at run time; kept Topwater's")
            return
        mode, mtime = e.mode & 0o7777, datetime.fromtimestamp(e.mtime, timezone.utc).replace(tzinfo=None)
        if e.isdir():
            self.items[path] = _Entry(True, mode, e.uid, mtime, None)
        elif e.isreg():
            self.bytes += e.size
            if self.bytes > self.max_fs:
                raise InvalidContainerData(f"the files to import hold more than {self.max_fs} bytes (max_file_system_bytes)")
            if data is None:
                data = self._data(tar, e)
            if not self.o.include_binaries and data.startswith(b"\x7fELF"):
                self.report.skip(path, "an ELF executable; Topwater can't run it (see include_binaries)")
                self.bytes -= e.size
                return
            self.items[path] = _Entry(False, mode, e.uid, mtime, data)
        elif e.issym():
            # kept as the link it is: it is only a path, resolved in the machine with the agent's permissions
            if not 0 < len(e.linkname) <= 4096 or "\0" in e.linkname:
                self.report.skip(path, "a symbolic link with an empty or overlong target")
            else:
                self.items[path] = _Entry(False, 0o777, e.uid, mtime, b"", e.linkname)
        elif e.islnk():
            self.links[path] = _canonical(e.linkname)
        else:
            kind = "character device" if e.ischr() else "block device" if e.isblk() else "fifo" if e.isfifo() else f"type {e.type!r}"
            self.report.skip(path, f"a {kind} entry, not a file or directory")

    @staticmethod
    def _data(tar: tarfile.TarFile, e: tarfile.TarInfo) -> bytes:
        """An entry's data, never more than its header claims."""
        f = tar.extractfile(e)
        data = f.read(e.size) if f is not None and e.size else b""
        if len(data) != e.size:
            raise InvalidContainerData(f"{e.name}: truncated")
        return data

    def finish(self) -> ImportResult:
        report = self.report
        self.resolve_links()

        # owners by the container's own /etc/passwd
        accounts = _parse_passwd(self.captured["/etc/passwd"].decode("utf-8", "replace") if "/etc/passwd" in self.captured else None)

        def owner(uid: int) -> str:
            n = next((a.name for a in accounts if a.uid == uid), None)
            return n if n is not None and _USER_NAME.fullmatch(n) else "root" if uid == 0 else str(uid)

        user = (self.config.user or "").split(":")[0]
        if not user:
            user = "root"
        elif user.isascii() and user.isdigit():
            user = owner(int(user))
        if not _USER_NAME.fullmatch(user):
            report.skip("user " + json.dumps(user), "not a valid user name; imported as root")
            user = "root"
        home = next((a.home for a in accounts if a.name == user), "/home/" + user)
        hostname = self.o.limits.get("hostname", "topwater")
        machine = _Machine.default(user, hostname)
        fs = machine.files

        for path in self.deleted:
            f = fs.get(path)
            if path in self.items or f is None:
                continue
            if _system_needs(path, f):
                report.skip(path, "deleted in the container, but Topwater's machine needs it")
            else:
                machine.remove_tree(path)
                report.deleted.append(path)

        for path, item in sorted(self.items.items()):
            # a crafted archive can hold entries below a symbolic link; a real filesystem can't
            p = get_parent(path)
            while p != "/" and (fs.get(p) is None or fs[p].link_target is None):
                p = get_parent(p)
            if p != "/":
                report.skip(path, f"below {p}, a symbolic link")
                continue
            self.ensure(machine, get_parent(path))
            old = fs.get(path)
            if old is not None and (old.is_dir != item.is_dir or item.link_target is not None):
                machine.remove_tree(path)
            binary = False
            if not item.is_dir and item.link_target is None:
                try:
                    item.data.decode("utf-8")
                except UnicodeDecodeError:
                    binary = True
            fs[path] = _Item(path, item.is_dir, item.mode, owner(item.uid), item.mtime, item.data, binary, link_target=item.link_target)
            report.carried.append(path)

        # an image Topwater exported: undo the export's additions, take the history back
        session = self.captured.get(SESSION_PATH)
        topwater = session is not None
        bashrc = home + "/.bashrc"
        rc = machine.text(bashrc)
        if topwater and rc is not None and BASHRC_HOOK in rc:
            at = rc.rindex(BASHRC_HOOK)
            fs[bashrc] = replace(fs[bashrc], data=(rc[:at] + rc[at + len(BASHRC_HOOK):]).encode())
        h = self.captured.get(home + "/.bash_history")
        history = _history(h.decode("utf-8", "replace")) if h is not None else []
        if topwater and fs.pop(home + "/.bash_history", None) is not None and home + "/.bash_history" in report.carried:
            report.carried.remove(home + "/.bash_history")

        for kv in self.config.env:
            eq = kv.find("=")
            name = kv[:eq] if eq > 0 else kv
            if eq > 0 and _VAR_NAME.fullmatch(name) and name not in MANAGED_VARIABLES:
                machine.set_exported(name, kv[eq + 1:])
            else:
                report.skip("environment " + json.dumps(name), "not a variable a shell can hold")

        cwd = normalize("/", self.config.working_dir) if self.config.working_dir else "/"
        self.ensure(machine, cwd)
        machine.set_exported("PWD", cwd)
        machine.manifest["cwd"] = cwd
        machine.manifest["history"] = history
        options = {**self.o.limits, "user": user, "hostname": hostname}
        from types import SimpleNamespace
        limits = SimpleNamespace(**options)
        snapshot = machine.to_snapshot(limits)

        if topwater and self.o.restore_session:
            # the session script runs in the simulated shell, like any agent command: the container's content never reaches the host
            shell = Shell(**options)
            shell.restore(snapshot)
            r = shell.run(session.decode("utf-8", "replace"))
            if r.exit_code != 0:
                report.skip(SESSION_PATH, f"the session script ended with exit code {r.exit_code}: {r.stderr.strip()}")
            after = _Machine.read(shell.snapshot())
            after.manifest["history"] = list(history)
            after.manifest["cwd"] = cwd
            for k in ("lastExitCode", "lastArg", "pipeStatus"):
                after.manifest.pop(k, None)
            snapshot = after.to_snapshot(limits)
        report.skip("file groups", "Topwater files have an owner but no group; the container's gids are dropped")
        return ImportResult(snapshot, report)

    def ensure(self, machine: _Machine, d: str) -> None:
        """Makes d and its parents directories, replacing files in the way."""
        if d == "/":
            return
        self.ensure(machine, get_parent(d))
        f = machine.files.get(d)
        if f is not None:
            if f.is_dir:
                return
            self.report.skip(d, "a file in Topwater's machine, a directory in the container; replaced")
        machine.files[d] = _Item(d, True, 0o755, "root", utc_now())

    def resolve_links(self) -> None:
        """Hard links whose target was imported become copies of it (a file, or a symbolic link); the others are skipped."""
        for link, target in self.links.items():
            t = target
            for _ in range(8):
                if t not in self.links:
                    break
                t = self.links[t]
            item = self.items.get(t)
            if item is None or item.is_dir:
                self.report.skip(link, f"a hard link to {target}, which wasn't imported")
                continue
            self.items[link] = item


def _history(text: str) -> list[str]:
    """A .bash_history: one command per line, or, with timestamps (#1700000000), one entry per timestamp."""
    lines = text.split("\n")
    if lines and lines[-1] == "":
        lines.pop()
    out: list[str] = []
    stamped = False
    for line in lines:
        if len(line) > 1 and line[0] == "#" and line[1:].isascii() and line[1:].isdigit():
            stamped = True
            out.append("")
            continue
        if stamped and out:
            out[-1] = line if not out[-1] else out[-1] + "\n" + line
        else:
            out.append(line)
    return [e for e in out if e]


# ---------- docker ----------

def docker_available() -> bool:
    """True when the docker CLI runs and reaches a daemon (within 10 seconds)."""
    try:
        _docker(["version", "--format", "{{.Server.Version}}"], timeout=10)
        return True
    except (ContainerError, OSError, subprocess.TimeoutExpired):
        return False


def _docker(args: list[str], stdin: bytes | None = None, read=None, timeout: float | None = None) -> str:
    """Runs docker with args (no shell). With `read`, its stdout is streamed to read(stream), else returned as text.
    Raises ContainerError with docker's error output when it exits non-zero; kills it if `read` raises."""
    with tempfile.TemporaryFile() as err:
        if read is None:
            try:
                p = subprocess.run([EXECUTABLE, *args], input=stdin, stdout=subprocess.PIPE, stderr=err, timeout=timeout)
            except FileNotFoundError as e:
                raise ContainerError(f"could not start {EXECUTABLE}") from e
            out, code = p.stdout.decode("utf-8", "replace"), p.returncode
        else:
            try:
                p = subprocess.Popen([EXECUTABLE, *args], stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=err)
            except FileNotFoundError as e:
                raise ContainerError(f"could not start {EXECUTABLE}") from e
            try:
                read(p.stdout)
                p.stdout.close()
                code = p.wait()
            except BaseException:
                p.kill()
                p.wait()
                raise
            out = ""
        if code != 0:
            err.seek(0)
            raise ContainerError(f"{EXECUTABLE} {' '.join(args[:2])} failed (exit {code}): {err.read().decode('utf-8', 'replace').strip()}")
        return out
