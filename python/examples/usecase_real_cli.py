"""Use case: let the agent drive a real command-line program.

Some tools are too big to re-implement and already speak "CLI": git, OfficeCLI, ffmpeg, your company's own
binaries. `register_host_command` exposes a real program on the host as a command in the simulation. The agent's
files are copied to a private mirror folder before each call and the program's changes are copied back, so the
agent keeps working in its virtual home while the real tool does the heavy lifting.

Here the agent is a release assistant using the real `git`:

    GEMINI_API_KEY=... python examples/usecase_real_cli.py

Wiring OfficeCLI (https://github.com/iOfficeAI/OfficeCLI) is the same one line, if it is installed:

    register_host_command(shell, "officecli")     # then: officecli create report.docx, officecli add ..., etc.

Read the warning in topwater/host.py first: a host program is real, so wrap only tools you trust the agent with.
"""
from __future__ import annotations

import shutil
import subprocess

from agent_loop import report, run_agent

from topwater import Shell
from topwater.host import register_host_command

GIT_ENV = {"GIT_AUTHOR_NAME": "Release Bot", "GIT_AUTHOR_EMAIL": "release-bot@example.com",
           "GIT_COMMITTER_NAME": "Release Bot", "GIT_COMMITTER_EMAIL": "release-bot@example.com",
           "GIT_CONFIG_NOSYSTEM": "1", "GIT_TERMINAL_PROMPT": "0"}

TASK = """You're preparing release 1.3.0 of the project in ~/project (a git repository).
1. Create a branch called release/1.3.0 from main.
2. Bump the version from 1.2.4 to 1.3.0 everywhere it appears (VERSION and src/config.ini).
3. In CHANGELOG.md, rename the "Unreleased" heading to "1.3.0 - 2026-11-24" and add a fresh empty "## Unreleased"
   section above it.
4. Commit everything with the message "Release 1.3.0" and tag the commit v1.3.0.
5. Tell me the short hash of the release commit and which files it changed."""


def setup(shell: Shell) -> None:
    for cmd in [
        "mkdir -p ~/project/src && cd ~/project && git init -q -b main",
        "echo 1.2.4 > VERSION",
        "printf '[app]\\nname = tidepool\\nversion = 1.2.4\\ndebug = false\\n' > src/config.ini",
        "printf '# Changelog\\n\\n## Unreleased\\n- Faster startup\\n- Fix crash on empty input\\n\\n## 1.2.4 - 2026-10-02\\n- Initial public release\\n' > CHANGELOG.md",
        "printf '# tidepool\\n\\nA small demo project.\\n' > README.md",
        "git add -A && git commit -q -m 'Initial commit' && cd ~",
    ]:
        r = shell.run(cmd)
        assert r.exit_code == 0, (cmd, r.stderr)


def main() -> None:
    if shutil.which("git") is None:
        raise SystemExit("this example needs git installed on the host")
    shell = Shell()
    git = register_host_command(shell, "git", env=GIT_ENV, description="the stupid content tracker")
    if shutil.which("officecli"):
        register_host_command(shell, "officecli", description="Create and edit Word, Excel and PowerPoint files")
    try:
        setup(shell)
        result = run_agent(shell, TASK)

        repo = str(git.root / "home/agent/project")

        def real_git(*args: str) -> str:
            return subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True, env={**GIT_ENV, "PATH": "/usr/bin:/bin"}).stdout.strip()

        fs = shell.fs
        version = fs.read_text("/home/agent/project/VERSION").strip() if fs.is_file("/home/agent/project/VERSION") else ""
        config = fs.read_text("/home/agent/project/src/config.ini") if fs.is_file("/home/agent/project/src/config.ini") else ""
        changelog = fs.read_text("/home/agent/project/CHANGELOG.md") if fs.is_file("/home/agent/project/CHANGELOG.md") else ""
        head = real_git("rev-parse", "--short", "release/1.3.0")
        changed = set(real_git("show", "--name-only", "--format=", "release/1.3.0").split())
        checks = [
            ("branch release/1.3.0 exists", bool(head)),
            ("commit message is 'Release 1.3.0'", real_git("log", "-1", "--format=%s", "release/1.3.0") == "Release 1.3.0"),
            ("tag v1.3.0 points at the release commit", real_git("rev-parse", "--short", "v1.3.0^{commit}") == head and bool(head)),
            ("VERSION is 1.3.0", version == "1.3.0"),
            ("config.ini version is 1.3.0", "version = 1.3.0" in config and "1.2.4" not in config),
            ("CHANGELOG has the dated 1.3.0 section under a fresh Unreleased",
             0 <= changelog.find("## Unreleased") < changelog.find("## 1.3.0 - 2026-11-24") < changelog.find("- Faster startup")),
            ("release commit changed exactly the three files", changed == {"VERSION", "src/config.ini", "CHANGELOG.md"}),
            ("main is untouched", real_git("log", "--format=%s", "main") == "Initial commit"),
            ("working tree is clean", real_git("status", "--porcelain") == ""),
            ("answer reports the commit hash", bool(head) and head[:7] in result.answer),
        ]
        ok = report("Real CLI (git)", checks, result)
        raise SystemExit(0 if ok else 1)
    finally:
        git.close()


if __name__ == "__main__":
    main()
