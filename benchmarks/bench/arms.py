"""The three arms. Each gives the agent loop a tool list and a call(name, args) -> text, and gives the checkers
read(path) and run(command) on the episode's machine. All of them start from the same fixtures and the same Shop.

  A  topwater   one `bash` tool on a Topwater shell (Python port); the shop is the `shop` program
  B  classic    one JSON tool per shop function, plus read_file / write_file / list_files / read_document / git
  C  docker     one `bash` tool on real bash in an Ubuntu container; `shop` and `to-markdown` call back to the host
"""
import base64
import io
import json
import os
import queue
import secrets
import shlex
import socketserver
import subprocess
import sys
import tarfile
import tempfile
import threading
from http.server import BaseHTTPRequestHandler
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT.parent / "python" / "src"))
import topwater  # noqa: E402
from topwater import Shell, ShellResult, convert  # noqa: E402
from topwater.tools import ToolSpec, ToolResult, register_tools  # noqa: E402

from .shop import GROUP_DESCRIPTION, SPECS, Shop  # noqa: E402

HOME = "/home/agent"
IMAGE = "twbench-ubuntu:1"
RUNDIR = ROOT / ".run"  # sockets, canaries; git-ignored
FIXTURES = ROOT / "fixtures" / "home"
SETUP_REPO = "(\n" + (ROOT / "fixtures" / "setup_repo.sh").read_text() + ")"  # a subshell: leaves no cd or set -e behind
SHOP_HINT = (" This machine has the `shop` program, the store's order, customer, inventory, shipment, refund and report API: "
             "run `shop --help` to list its subcommands and `shop SUBCOMMAND --help` for each one's options.")
BASH_SCHEMA = topwater.TOOL_INPUT_SCHEMA
DESC_A = topwater.TOOL_DESCRIPTION + SHOP_HINT
DESC_C = ("Run a command in a sandboxed Linux shell (bash) on Ubuntu 24.04. The working directory, environment variables and files "
          "persist between calls. Standard command-line tools are available (ls, cat, grep, sed, awk, jq, find, sort, diff, tar, git, ...); "
          "git works on local repositories. Python, network access and package installation are not. Read Word, Excel and "
          "PowerPoint files with `to-markdown FILE`." + SHOP_HINT)


def fixture_files():
    """(path in the machine, bytes) for every fixture; app.log.txt becomes app.log (the repository ignores *.log)."""
    for f in sorted(FIXTURES.rglob("*")):
        if f.is_file():
            rel = f.relative_to(FIXTURES).as_posix()
            yield f"{HOME}/{rel[:-4] if rel.endswith('.log.txt') else rel}", f.read_bytes()


def tool(name, description, parameters):
    return {"type": "function", "function": {"name": name, "description": description, "parameters": parameters}}


def specs():
    return [ToolSpec(n, d, s) for n, d, s in SPECS]


def shop_shell(shop: Shop) -> Shell:
    def invoke(name, args):
        text, err = shop.invoke(name, args)
        return ToolResult(text + "\n", is_error=err)
    sh = Shell(hostname="sandbox")
    register_tools(sh, "shop", specs(), invoke, description=GROUP_DESCRIPTION)
    return sh


def topwater_machine(shop: Shop) -> Shell:
    sh = shop_shell(shop)
    for path, data in fixture_files():
        sh.fs.mkdir(os.path.dirname(path), parents=True)
        sh.fs.write_bytes(path, data)
    sh.fs.set_owner(HOME, "agent", recursive=True)
    r = sh.run(SETUP_REPO)
    if r.exit_code != 0:
        raise RuntimeError(f"repo setup failed on Topwater: {r.stderr}")
    return sh


def expand(path: str) -> str:
    path = (path or "").strip()
    if path == "~" or path.startswith("~/"):
        path = HOME + path[1:]
    return path if path.startswith("/") else f"{HOME}/{path}"


class TopwaterArm:
    name = "A"

    def __init__(self, shop: Shop):
        self.shop, self.sh = shop, topwater_machine(shop)
        self.tools = [tool("bash", DESC_A, BASH_SCHEMA)]

    def call(self, name, args):
        if name != "bash" or not isinstance(args.get("command"), str):
            return "error: the only tool is bash, with a string argument named command"
        return self.sh.run(args["command"]).to_tool_output()

    def read(self, path):
        path = expand(path)
        return self.sh.fs.read_text(path) if self.sh.fs.is_file(path) else None

    def run(self, command):
        return self.sh.run(command).stdout

    def close(self):
        pass


class ClassicArm(TopwaterArm):
    """Function calling: the same shop functions and the same files, no shell. Files live in a Topwater filesystem that
    only these tools touch, so the data is byte-for-byte what arm A sees."""
    name = "B"
    MAX = 30_000

    def __init__(self, shop: Shop):
        super().__init__(shop)
        path = {"type": "string", "description": "Absolute path, or relative to the home directory /home/agent; ~ also means /home/agent."}
        self.tools = [tool(n, d, s) for n, d, s in SPECS] + [
            tool("list_files", "List a directory: one line per entry with its type (file or dir), size in bytes and path. "
                               "The task's files are under the home directory /home/agent.",
                 {"type": "object", "properties": {"path": path, "recursive": {"type": "boolean", "description": "Include everything below, not just the directory's own entries (default false)."}}, "required": ["path"]}),
            tool("read_file", "Read a text file. Returns its lines; use offset and limit to page through long files. Output beyond 30,000 characters is cut.",
                 {"type": "object", "properties": {"path": path, "offset": {"type": "integer", "description": "First line to return, counting from 1 (default 1)."},
                                                   "limit": {"type": "integer", "description": "How many lines to return (default: all)."}}, "required": ["path"]}),
            tool("write_file", "Create or overwrite a text file with the given content. Missing parent directories are created.",
                 {"type": "object", "properties": {"path": path, "content": {"type": "string", "description": "The complete new content of the file."}}, "required": ["path", "content"]}),
            tool("read_document", "Read a Word, Excel or PowerPoint file (.docx, .xlsx, .pptx) as Markdown.",
                 {"type": "object", "properties": {"path": path}, "required": ["path"]}),
            tool("git", "Run git in a local repository (no network), e.g. args [\"log\", \"--oneline\"]. Supports log, show, diff, status, add, commit, grep, tag and the like.",
                 {"type": "object", "properties": {"args": {"type": "array", "items": {"type": "string"}, "description": "The arguments after `git`."},
                                                   "cwd": {"type": "string", "description": "The repository directory."}}, "required": ["args", "cwd"]}),
        ]
        self.names = {n for n, _, _ in SPECS}

    def _clip(self, s):
        return s if len(s) <= self.MAX else s[:self.MAX] + f"\n... [output truncated, {len(s) - self.MAX} more characters]\n"

    def call(self, name, args):
        try:
            return self._call(name, args)
        except Exception as e:  # a bad path, a directory where a file was expected, ...
            return f"Error: {e}"

    def _call(self, name, args):
        fs = self.sh.fs
        if name in self.names:
            text, err = self.shop.invoke(name, args)
            return ("Error: " + text) if err else text
        if name == "list_files":
            root = expand(args.get("path", "~"))
            if not fs.is_dir(root):
                return f"Error: {root} is not a directory"
            out, stack = [], [root]
            while stack:
                d = stack.pop(0)
                for entry in sorted(fs.listdir(d)):
                    full = d.rstrip("/") + "/" + entry
                    isdir = fs.is_dir(full)
                    out.append(f"{'dir ' if isdir else 'file'} {0 if isdir else fs.stat(full).size:>9} {full}")
                    if isdir and args.get("recursive"):
                        stack.append(full)
            return self._clip("\n".join(out) or "(empty directory)")
        if name == "read_file":
            path = expand(args.get("path", ""))
            if not fs.is_file(path):
                return f"Error: {path}: no such file"
            data = fs.read_bytes(path)
            if b"\0" in data[:4096] or data[:2] == b"PK":
                return f"Error: {path} is a binary file" + (" (use read_document)" if path.endswith((".docx", ".xlsx", ".pptx")) else "")
            lines = data.decode("utf-8", "replace").splitlines(keepends=True)
            start = max(1, int(args.get("offset") or 1))
            limit = args.get("limit")
            chunk = lines[start - 1: start - 1 + int(limit)] if limit else lines[start - 1:]
            return self._clip("".join(chunk)) or "(empty)"
        if name == "write_file":
            path = expand(args.get("path", ""))
            fs.mkdir(os.path.dirname(path), parents=True)
            content = args.get("content", "")
            fs.write_text(path, content if isinstance(content, str) else json.dumps(content))
            return f"wrote {len(fs.read_bytes(path))} bytes to {path}"
        if name == "read_document":
            path = expand(args.get("path", ""))
            if not fs.is_file(path):
                return f"Error: {path}: no such file"
            return self._clip(convert.to_markdown(fs.read_bytes(path), path))
        if name == "git":
            a = args.get("args")
            if not isinstance(a, list) or not all(isinstance(x, str) for x in a):
                return "Error: args must be an array of strings"
            return self.sh.run(f"cd {shlex.quote(expand(args.get('cwd', '~')))} && git {shlex.join(a)}").to_tool_output()
        return f"Error: unknown tool {name}"


# ---------- arm C ----------
class _Bridge(socketserver.ThreadingMixIn, socketserver.UnixStreamServer):
    daemon_threads = True


def _bridge_handler(shop_sh: Shell, lock: threading.Lock):
    class H(BaseHTTPRequestHandler):
        def address_string(self):
            return "container"

        def log_message(self, *a):
            pass

        def do_POST(self):
            n = int(self.headers.get("Content-Length") or 0)
            if n > 32 << 20:
                return self._reply({"stdout": "", "stderr": "request too large\n", "exit": 1})
            body = json.loads(self.rfile.read(n) or b"null")
            with lock:  # one call at a time, like the shell itself
                if self.path == "/shop" and isinstance(body, list) and all(isinstance(x, str) for x in body):
                    r = shop_sh.run("shop " + shlex.join(body))
                elif self.path == "/to-markdown" and isinstance(body, dict):
                    sh = Shell()
                    for p, b64 in (body.get("files") or {}).items():
                        sh.fs.mkdir(os.path.dirname(p), parents=True)
                        sh.fs.write_bytes(p, base64.b64decode(b64))
                    cwd = body.get("cwd") or HOME
                    sh.fs.mkdir(cwd, parents=True)
                    r = sh.run(f"cd {shlex.quote(cwd)} && to-markdown {shlex.join(body.get('args') or [])}")
                else:
                    r = ShellResult("", "bad request\n", 2)
            self._reply({"stdout": r.stdout, "stderr": r.stderr, "exit": r.exit_code})

        def _reply(self, obj):
            data = json.dumps(obj).encode()
            self.send_response(200)
            self.send_header("Content-Length", str(len(data)))
            self.end_headers()
            self.wfile.write(data)
    return H


class DockerArm:
    """Real bash. One container per episode; each tool call is one line on the shell's stdin, its output comes back
    base64-encoded after a per-episode marker, so command output can't be confused with the protocol."""
    name = "C"
    CALL_TIMEOUT = 130  # Topwater's own limit is 120 s
    PRELUDE = r"""
__tw_run() { eval "$(printf %s "$1" | base64 -d)" </dev/null >/tmp/.tw_o 2>/tmp/.tw_e; local ec=$?; printf '%s %s\n' "$__TW_MARK" "$ec"; head -c 1000000 /tmp/.tw_o | base64 -w0; echo; head -c 200000 /tmp/.tw_e | base64 -w0; echo; rm -f /tmp/.tw_o /tmp/.tw_e; }
__tw_load() { printf %s "$1" | base64 -d | tar xzf - -C /home/agent; }
"""

    def __init__(self, shop: Shop, tag: str):
        self.shop, self.tag, self.restarts = shop, tag, 0
        self.tools = [tool("bash", DESC_C, BASH_SCHEMA)]
        RUNDIR.mkdir(exist_ok=True)
        self.tmp = tempfile.mkdtemp(prefix="s", dir=RUNDIR)  # short: a Unix socket path must stay under 108 bytes
        os.chmod(self.tmp, 0o755)
        self.server = _Bridge(os.path.join(self.tmp, "app.sock"), _bridge_handler(shop_shell(shop), threading.Lock()))
        os.chmod(os.path.join(self.tmp, "app.sock"), 0o777)
        threading.Thread(target=self.server.serve_forever, daemon=True).start()
        self.tarball = self._fixture_tar()
        self._start()

    @staticmethod
    def _fixture_tar():
        buf = io.BytesIO()
        with tarfile.open(fileobj=buf, mode="w:gz") as t:
            dirs = set()
            for path, data in fixture_files():
                rel = path[len(HOME) + 1:]
                parts = rel.split("/")[:-1]
                for i in range(1, len(parts) + 1):
                    d = "/".join(parts[:i])
                    if d not in dirs:
                        dirs.add(d)
                        info = tarfile.TarInfo(d)
                        info.type, info.mode, info.uid, info.gid, info.mtime = tarfile.DIRTYPE, 0o755, 1000, 1000, 1767225600
                        t.addfile(info)
                info = tarfile.TarInfo(rel)
                info.size, info.mode, info.uid, info.gid, info.mtime = len(data), 0o644, 1000, 1000, 1767225600
                t.addfile(info, io.BytesIO(data))
        return base64.b64encode(buf.getvalue()).decode()

    def _start(self):
        self.container = f"twbench-{self.tag}-{secrets.token_hex(3)}"
        self.mark = "__TW_" + secrets.token_hex(8)
        cmd = ["timeout", "3600", "docker", "run", "--rm", "-i", "--init", "--name", self.container, "--log-driver", "none",
               "--network", "none", "--memory", "512m", "--memory-swap", "512m", "--pids-limit", "256", "--cpus", "1",
               "--cap-drop", "ALL", "--security-opt", "no-new-privileges", "--read-only",
               "--tmpfs", "/tmp:rw,exec,size=64m,mode=1777", "--tmpfs", "/home/agent:rw,exec,size=128m,uid=1000,gid=1000,mode=755",
               "--hostname", "sandbox", "-e", "HOME=/home/agent", "-e", "USER=agent", "-e", "LANG=C.UTF-8",
               "-v", f"{self.tmp}:/run/app:ro", IMAGE, "bash"]
        self.proc = subprocess.Popen(cmd, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, env={k: os.environ[k] for k in ("PATH", "HOME", "DOCKER_HOST") if k in os.environ})
        self.lines = queue.Queue()
        threading.Thread(target=self._pump, args=(self.proc, self.lines), daemon=True).start()
        self._send(f"__TW_MARK={self.mark}\n{self.PRELUDE}\n__tw_load {self.tarball}\n")
        r = self._exec(SETUP_REPO)
        if r is None or r.exit_code != 0:
            raise RuntimeError(f"container setup failed: {r}")

    @staticmethod
    def _pump(proc, q):
        for line in proc.stdout:
            q.put(line.decode("utf-8", "replace").rstrip("\n"))
        q.put(None)

    def _send(self, text):
        self.proc.stdin.write(text.encode())
        self.proc.stdin.flush()

    def _exec(self, command):
        """Runs one command line; None if the shell died or timed out."""
        try:
            self._send(f"__tw_run {base64.b64encode(command.encode()).decode()}\n")
            while True:
                line = self.lines.get(timeout=self.CALL_TIMEOUT)
                if line is None:
                    return None
                if line.startswith(self.mark + " "):
                    break
            ec = int(line.split()[1])
            out = base64.b64decode(self.lines.get(timeout=30) or "").decode("utf-8", "replace")
            err = base64.b64decode(self.lines.get(timeout=30) or "").decode("utf-8", "replace")
            return ShellResult(out, err, ec)
        except (queue.Empty, BrokenPipeError, OSError, ValueError, TypeError):
            return None

    def _stop(self):
        try:
            self.proc.terminate()
            self.proc.wait(timeout=20)
        except Exception:
            self.proc.kill()
        subprocess.run(["docker", "rm", "-f", self.container], capture_output=True, timeout=60)

    def call(self, name, args):
        if name != "bash" or not isinstance(args.get("command"), str):
            return "error: the only tool is bash, with a string argument named command"
        r = self._exec(args["command"])
        if r is None:  # the shell exited (`exit`, `set -e`) or the call ran too long: start over from the fixtures
            self._stop()
            self.restarts += 1
            self._start()
            return "[the shell exited or the command timed out; a new shell was started from the original files, and earlier changes are lost]"
        return r.to_tool_output()

    def read(self, path):
        r = self._exec(f"base64 -w0 {shlex.quote(expand(path))}")
        if r is None or r.exit_code != 0:
            return None
        return base64.b64decode(r.stdout).decode("utf-8", "replace")

    def run(self, command):
        r = self._exec(command)
        return "" if r is None else r.stdout

    def close(self):
        self._stop()
        self.server.shutdown()
        self.server.server_close()
        try:
            os.remove(os.path.join(self.tmp, "app.sock"))
            os.rmdir(self.tmp)
        except OSError:
            pass


def make_arm(arm: str, shop: Shop, tag: str):
    return {"A": lambda: TopwaterArm(shop), "B": lambda: ClassicArm(shop), "C": lambda: DockerArm(shop, tag)}[arm]()
