#!/usr/bin/env python3
"""Record a corpus case from the real tools: run SCRIPT in real bash and append its output to tests/corpus/AREA.jsonl.

    python3 tests/corpus/record.py AREA [--local] [--agent] [--apt PKG,...] [--tags a,b] [--notes TEXT] [--dry-run] SCRIPT

By default the script runs in a fresh ubuntu:24.04 docker container (bash 5.2.21, GNU coreutils 9.4), as `bash -c` in an
empty /tmp/w with TZ=UTC, LC_ALL=C.UTF-8 and no network. --local uses this machine's bash in an empty temp directory.
--agent runs it as the ordinary user `agent` (uid 1000, owning /tmp/w) like Topwater's shell, instead of root: for
`ls -l` owners and "Permission denied" cases. --git uses an image with git 2.43 (built once as topwater-git-ref) and stores
GIT_SETUP as the case's setup: fixed author, committer and dates, so commit ids are the same everywhere.
"""
import argparse, json, os, re, shutil, subprocess, sys, tempfile

CORPUS = os.path.dirname(os.path.abspath(__file__))
IMAGE = "ubuntu:24.04"
GIT_IMAGE = "topwater-git-ref"
GIT_DOCKERFILE = ("FROM ubuntu:24.04\nRUN apt-get -qq update >/dev/null && DEBIAN_FRONTEND=noninteractive apt-get -qq install -y git >/dev/null"
                  " && rm -rf /var/lib/apt/lists/*\n")
GIT_SETUP = ("export GIT_AUTHOR_NAME='A U Thor' GIT_AUTHOR_EMAIL=author@example.com GIT_COMMITTER_NAME='A U Thor' "
             "GIT_COMMITTER_EMAIL=author@example.com GIT_AUTHOR_DATE='2026-01-02T03:04:05Z' GIT_COMMITTER_DATE='2026-01-02T03:04:05Z'")
TIMEOUT = 60  # AGENTS.md: probes run under a time limit, without container logs


def git_image():
    if subprocess.run(["docker", "image", "inspect", GIT_IMAGE], capture_output=True).returncode != 0:
        subprocess.run(["docker", "build", "-q", "-t", GIT_IMAGE, "-"], input=GIT_DOCKERFILE.encode(), check=True, timeout=600)
    return GIT_IMAGE


def run(script, local, apt, agent=False, image=IMAGE):
    env = {"TZ": "UTC", "LC_ALL": "C.UTF-8"}
    if local:
        with tempfile.TemporaryDirectory() as d:
            w = os.path.join(d, "w")
            os.mkdir(w)
            full = {"PATH": os.environ.get("PATH", "/usr/bin:/bin"), "HOME": w, **env}
            return subprocess.run(["bash", "-c", script], cwd=w, env=full, capture_output=True, timeout=TIMEOUT)
    if not shutil.which("docker"):
        sys.exit("record.py: docker not found; use --local")
    prep = ""
    if apt:
        # apt needs the network, so installing runs as a separate step before the script runs offline
        pkgs = " ".join(apt)
        prep = f"apt-get update -qq >/dev/null && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq {pkgs} >/dev/null 2>&1 && "
    net = [] if apt else ["--network", "none"]
    start = 'mkdir -p /tmp/w && cd /tmp/w && exec timeout 5 bash -c "$1"'
    if agent:
        # the image's uid-1000 user, renamed to match Topwater's
        start = ("usermod -l agent -d /home/agent -m ubuntu && groupmod -n agent ubuntu && mkdir -p /tmp/w && chown agent: /tmp/w"
                 ' && cd /tmp/w && exec timeout 5 setpriv --reuid=agent --regid=agent --init-groups env HOME=/home/agent bash -c "$1"')
    cmd = ["docker", "run", "--rm", "--log-driver", "none", "-i", *net, *sum((["-e", f"{k}={v}"] for k, v in env.items()), []), image,
           "bash", "-c", prep + start, "_", script]
    return subprocess.run(cmd, capture_output=True, timeout=TIMEOUT + (300 if apt else 0))


def main():
    p = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    p.add_argument("area", help="corpus file name without .jsonl, e.g. quoting")
    p.add_argument("script")
    p.add_argument("--local", action="store_true", help="use this machine's bash instead of docker")
    p.add_argument("--agent", action="store_true", help="run as the ordinary user agent (uid 1000), not root")
    p.add_argument("--git", action="store_true", help="use the git image and the fixed git identity and dates (GIT_SETUP)")
    p.add_argument("--apt", default="", help="comma-separated packages to install in the container first (e.g. bc,zip)")
    p.add_argument("--tags", default="")
    p.add_argument("--notes", default="")
    p.add_argument("--dry-run", action="store_true", help="print the case without appending it")
    a = p.parse_args()
    if not re.fullmatch(r"[a-z0-9-]+", a.area):
        sys.exit("record.py: area must be lowercase letters, digits and dashes")

    path = os.path.join(CORPUS, a.area + ".jsonl")
    cases = []
    if os.path.exists(path):
        with open(path, encoding="utf-8") as f:
            cases = [json.loads(l) for l in f if l.strip()]
    if not a.dry_run and any(c["script"] == a.script for c in cases):
        sys.exit(f"record.py: {a.area} already has this script")

    if a.agent and a.local:
        sys.exit("record.py: --agent needs docker")
    setup = GIT_SETUP if a.git else None
    if a.git and a.local:
        sys.exit("record.py: --git needs docker")
    r = run(setup + "\n" + a.script if setup else a.script, a.local, [x for x in a.apt.split(",") if x], a.agent,
            git_image() if a.git else IMAGE)
    try:
        stdout = r.stdout.decode("utf-8")
    except UnicodeDecodeError:
        sys.exit("record.py: stdout is not UTF-8 text; compare a listing or a checksum instead")

    nums = [int(m.group(1)) for c in cases if (m := re.fullmatch(re.escape(a.area) + r"-(\d+)", c["id"]))]
    case = {"id": f"{a.area}-{max(nums, default=0) + 1:03d}", **({"setup": setup} if setup else {}), "script": a.script, "stdout": stdout,
            "exitCode": r.returncode, "quiet": not r.stderr}
    if a.tags:
        case["tags"] = a.tags.split(",")
    if a.notes:
        case["notes"] = a.notes
    line = json.dumps(case, ensure_ascii=False, separators=(",", ":"))
    if r.stderr:
        print("stderr: " + r.stderr.decode("utf-8", "replace").rstrip(), file=sys.stderr)
    print(line)
    if not a.dry_run:
        with open(path, "a", encoding="utf-8") as f:
            f.write(line + "\n")


if __name__ == "__main__":
    main()
