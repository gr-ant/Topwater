#!/usr/bin/env python3
"""Records tests/web/cases.jsonl from real curl 8.5.0 and wget 1.21.4 on ubuntu:24.04.

    python3 tests/web/record.py            # re-record every case (docker; builds the image topwater-net-ref once)
    python3 tests/web/record.py web-007    # only these ids

Each case runs as `bash -c` in an empty /tmp/w, LC_ALL=C.UTF-8, in a container without a network where api.test
is 127.0.0.1 and server.py serves routes.json on port 80. Other names do not resolve, which is what Topwater's web
policy makes of hosts it does not allow. Only stdout, stderr and the exit status are stored; "stderr": null in a
case means stderr is not compared (wget's progress report has times and speeds). A case with a "note" is Topwater's
own behaviour where it differs from curl or wget on purpose (the note says how): it is not recorded, only printed
beside what the real tool does.
"""
import json, os, subprocess, sys

HERE = os.path.dirname(os.path.abspath(__file__))
IMAGE = "topwater-net-ref"
DOCKERFILE = ("FROM ubuntu:24.04\nRUN apt-get update && apt-get install -y --no-install-recommends curl wget ca-certificates python3"
              " && rm -rf /var/lib/apt/lists/*\n")
INSIDE = r'''
import json, subprocess, sys, shutil, os, time
subprocess.Popen(["python3", "/web/server.py", "/web/routes.json", "80"])
time.sleep(0.5)
out = []
for case in json.load(sys.stdin):
    shutil.rmtree("/tmp/w", ignore_errors=True); os.makedirs("/tmp/w")
    p = subprocess.run(["bash", "-c", case["script"]], cwd="/tmp/w", capture_output=True, timeout=20,
                       env={"PATH": "/usr/bin:/bin", "LC_ALL": "C.UTF-8", "HOME": "/root"})
    out.append({"stdout": p.stdout.decode("utf-8", "surrogateescape"), "stderr": p.stderr.decode("utf-8", "surrogateescape"), "exitCode": p.returncode})
print(json.dumps(out))
'''


def main():
    path = os.path.join(HERE, "cases.jsonl")
    cases = [json.loads(l) for l in open(path) if l.strip()]
    only = set(sys.argv[1:])
    todo = [c for c in cases if not only or c["id"] in only]
    if subprocess.run(["docker", "image", "inspect", IMAGE], capture_output=True).returncode:
        subprocess.run(["timeout", "600", "docker", "build", "-t", IMAGE, "-"], input=DOCKERFILE.encode(), check=True)
    r = subprocess.run(["timeout", "600", "docker", "run", "--rm", "-i", "--log-driver", "none", "--network", "none",
                        "--add-host", "api.test:127.0.0.1", "-v", f"{HERE}:/web:ro", IMAGE, "python3", "-c", INSIDE],
                       input=json.dumps([{"script": c["script"]} for c in todo]).encode(), capture_output=True, check=True)
    for c, res in zip(todo, json.loads(r.stdout)):
        if "note" in c:
            print(f"{c['id']} (not recorded: {c['note']}): {json.dumps(res)}")
            continue
        keep_stderr = c.get("stderr", "") is not None
        c.update(res)
        if not keep_stderr:
            c["stderr"] = None
    with open(path, "w") as f:
        for c in cases:
            f.write(json.dumps(c, ensure_ascii=False) + "\n")
    print(f"recorded {len(todo)} cases")


main()
