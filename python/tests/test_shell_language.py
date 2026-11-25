"""Ported from main's ShellLanguageTests, SecondReviewTests (shell parts), BashCompatClock and the realism tests in
AgentFeatureTests: behaviour the shared corpus cannot express (host state, limits, persistence)."""
import time

import pytest

from bashcompat import Shell, check

SECOND_STOPSINSTEADOFHANGING = [
    ('while :; do while :; do :; done; done; echo survived'),
    ('f(){ f; f; }; f; echo survived'),
    ('x=\'eval "$x"\'; eval "$x"; echo survived'),
    ("printf './a.sh\\n' > a.sh; chmod +x a.sh; ./a.sh; echo survived"),
    ("echo a | sed ':a;s/x*/x/;ta'"),
    ("jq -n '[range(1e10)] | length'"),
    ("jq -n '[1|recurse(.+1)] | length'"),
]
LANG_RUNAWAYCONSTRUCTSEND = [
    ("trap 'false' ERR; false; echo survived"),
    ("trap 'echo d >/dev/null' DEBUG; f() { :; }; f; f; echo survived"),
    ('declare -n a=b; declare -n b=a; echo "[$a]"; echo survived'),
    ('echo {1..10000}{1..10000} >/dev/null; echo survived'),
    ('while :; do wait; done; echo survived'),
    ("f() { f; }; trap 'echo r >/dev/null' RETURN; f; echo survived"),
]


def run(cmd: str, shell: Shell | None = None):
    return (shell or Shell()).run(cmd)


@pytest.mark.parametrize("cmd", [c[0] for c in SECOND_STOPSINSTEADOFHANGING])
def test_stops_instead_of_hanging(cmd):
    start = time.monotonic()
    r = run(cmd)
    assert time.monotonic() - start < 60
    assert r.exit_code != 0
    assert "survived" not in r.stdout
    assert r.stderr != ""


def test_long_output_keeps_the_error_and_exit_code():
    text = run("seq 1 20000; nosuchcmd").to_tool_output()
    assert "command not found" in text
    assert text.endswith("[exit code 127]")
    assert len(text) < 31_000


@pytest.mark.parametrize("open_,close,depth", [
    ("((", "))", 200_000),  # arithmetic
    ("( ", " )", 50_000),  # subshells
    ("{ ", "; }", 50_000),  # groups
    ("if ", "; then :; fi", 20_000),
    ("echo $((", "))", 100_000),
    ("echo $((-", "))", 1),  # control: shallow input still parses
])
def test_deep_nesting_is_an_error_not_a_crash(open_, close, depth):
    body = "1" if depth == 1 else "true"
    r = run(open_ * depth + body + close * depth)
    if depth == 1:
        assert r.stdout == "-1\n"
    else:
        assert r.stderr != ""


def test_host_variables_are_exported_and_shell_variables_are_not():
    sh = Shell()
    sh.env["API_URL"] = "https://example.test"
    r = sh.run("local_only=1; bash -c 'echo \"[$API_URL] [$local_only] [$HOME]\"'; export local_only; bash -c 'echo $local_only'")
    assert r.stdout == "[https://example.test] [] [/home/agent]\n1\n"
    assert sh.run("export -n API_URL; bash -c 'echo \"[$API_URL]\"'").stdout == "[]\n"


@pytest.mark.parametrize("cmd", [c[0] for c in LANG_RUNAWAYCONSTRUCTSEND])
def test_runaway_constructs_end(cmd):
    assert "internal error" not in run(cmd).stderr


def test_exec_redirection_lasts_for_the_call_and_scripts_keep_their_own():
    sh = Shell()
    r = sh.run("exec 2>&1; ls /nope; printf 'exec >inner.log\\necho hidden\\n' > s.sh; bash s.sh; echo shown; cat inner.log")
    assert r.stdout == "ls: cannot access '/nope': No such file or directory\nshown\nhidden\n"
    assert r.stderr == ""
    assert sh.run("echo err >&2").stderr == "err\n"  # the next tool call starts with its own stdout and stderr


def test_options_reset_between_calls_but_variables_persist():
    sh = Shell()
    sh.run("set -euxf -o noclobber; declare -i n=2; readonly R=1; shopt -s nocasematch")
    r = sh.run("echo $- ; n+=3; echo $n; [[ A == a ]] && echo nocase; R=2")
    assert r.stdout == "hBc\n5\nnocase\n"
    assert r.exit_code == 1
    assert "R: readonly variable" in r.stderr


# ---------- one UTC clock (BashCompatClock) ----------

def test_mtime_matches_date():
    check("touch f; now=$(date +%s); m=$(date -r f +%s); d=$(( now - m )); if [ $d -ge -2 ] && [ $d -le 2 ]; then echo ok; "
          "else echo \"skew=$d\"; fi", "ok\n", 0, True)


def test_uptime_hour_matches_date():
    check("h1=$(date -u +%H); h2=$(uptime | grep -oE '^ *[0-9]{2}' | tr -d ' '); [ \"$h1\" = \"$h2\" ] && echo ok || echo \"$h1 vs $h2\"",
          "ok\n", 0, True)


# ---------- a machine that looks lived in (AgentFeatureTests: RealismTests, LivedInMachineTests) ----------

def test_shell_builtins_are_not_program_files_and_system_files_are_old():
    sh = Shell().register_command("orders", lambda ctx: 0)
    bin_ = sh.run("ls /bin").stdout.split("\n")
    for name in ("eval", "cd", "export", "break", "source", "history"):
        assert name not in bin_
    for name in ("ls", "grep", "test", "echo", "orders"):
        assert name in bin_
    assert sh.run("eval 'echo ok'; cd /tmp && export X=1 && type cd >/dev/null").stdout == "ok\n"
    # nothing outside the agent's own areas looks recently modified
    sh.run("echo hi > ~/new.txt")
    found = sh.run("find / -mmin -120 -not -path '/tmp*' -not -path '/var/tmp*' -not -path '/proc*' -not -path '/sys' "
                   "-not -path '/run' 2>/dev/null | sort").stdout
    assert found == "/home/agent\n/home/agent/new.txt\n"


def test_the_machine_has_the_usual_clutter():
    sh = Shell().register_command("orders", lambda ctx: 0)

    def out(c: str) -> str:
        return sh.run(c).stdout

    assert "HISTSIZE=1000" in out("cat ~/.bashrc")
    assert out("ls -A ~") == ".bash_logout\n.bashrc\n.profile\n"
    assert out("ls ~") == ""
    for d in ("proc", "sys", "lib", "sbin", "run", "boot"):
        assert d in out("ls /").split("\n")
    assert out("grep -c ^processor /proc/cpuinfo") == "4\n"
    assert out("nproc") == out("grep -c ^processor /proc/cpuinfo")
    assert "8014880" in out("grep MemTotal /proc/meminfo")
    assert "topwater" in out("cat /etc/hosts")
    assert "agent:x:1000:" in out("cat /etc/group")
    # programs are not all the same suspicious 16 bytes
    grep, orders = int(out("stat -c %s /usr/bin/grep")), int(out("stat -c %s /usr/bin/orders"))
    assert grep > 10_000 and orders > 10_000 and grep != orders
