"""Ported from main's ArrayTests, ShellLanguageTests, SecondReviewTests (shell parts), BashCompatClock and the
realism tests in AgentFeatureTests: behaviour the BashCompat tables cannot express (host state, limits, persistence)."""
import time

import pytest

from bashcompat import Shell, check

ARRAY_CASES = [
    ('a=(x \'y z\' w); echo ${#a[@]} "${a[1]}" $a', '3 y z x\n'),
    ('a=(x \'y z\'); for e in "${a[@]}"; do echo "[$e]"; done', '[x]\n[y z]\n'),
    ('a=(x \'y z\'); for e in ${a[@]}; do echo "[$e]"; done', '[x]\n[y]\n[z]\n'),
    ('a=(x \'y z\'); printf \'%s|\' "${a[*]}"; echo', 'x y z|\n'),
    ('a=(); echo ${#a[@]}; for e in "${a[@]}"; do echo no; done; echo end', '0\nend\n'),
    ('a=(1 2); a+=(3 4); a+=(5); echo "${a[@]}"', '1 2 3 4 5\n'),
    ('a=(); for i in 1 2 3; do a+=("n $i"); done; echo ${#a[@]} "${a[2]}"', '3 n 3\n'),
    ('a[2]=c; a[0]=a; echo "${a[@]}" ${#a[@]} "${!a[@]}"', 'a c 2 0 2\n'),
    ('a=(a b c d); echo "${a[-1]}" "${a[@]:1:2}" "${a[@]:2}"', 'd b c c d\n'),
    ('a=(a b c); i=1; echo ${a[i]} ${a[$i]} ${a[i+1]} ${a[${#a[@]}-1]}', 'b b c c\n'),
    ('a=(a b c); for i in "${!a[@]}"; do echo "$i=${a[$i]}"; done', '0=a\n1=b\n2=c\n'),
    ('a=(a b c); for ((i=0; i<${#a[@]}; i++)); do printf \'%s\' "${a[i]}"; done; echo', 'abc\n'),
    ('a=(a b c); unset \'a[1]\'; echo "${a[@]}" ${#a[@]}; unset a; echo "[${a[@]}]"', 'a c 2\n[]\n'),
    ('a=(one two); a[1]=TWO; a[5]=six; echo "${a[@]}"; echo ${#a[1]}', 'one TWO six\n3\n'),
    ('a=(a.txt b.txt); echo "${a[@]%.txt}" "${a[@]/#/x-}" "${a[@]^^}"', 'a b x-a.txt x-b.txt A.TXT B.TXT\n'),
    ('a=($(echo one two three)); echo ${#a[@]} ${a[2]}', '3 three\n'),
    ('touch f1.log f2.log; a=(*.log); echo ${#a[@]} "${a[@]}"', '2 f1.log f2.log\n'),
    ('a=(\n  one   # comment\n  "two words"\n)\necho ${#a[@]} "${a[1]}"', '2 two words\n'),
    ('a=(1 2 3); b=("${a[@]}" 4); echo "${b[@]}"; c=("${a[@]:1}"); echo "${c[@]}"', '1 2 3 4\n2 3\n'),
    ('a=(3 4); echo $(( a[0] + a[1] )) $(( ${a[0]} * 2 ))', '7 6\n'),
    ('a=(1 2); (( a[0]++ )); (( a[1] += 10 )); echo "${a[@]}"', '2 12\n'),
    ('a=(x); echo "${a[5]:-none}" "${a[0]:-none}" "${a[5]+set}|"', 'none x |\n'),
    ('x=scalar; echo "${x[0]}" "${x[@]}" ${#x[@]}', 'scalar scalar 1\n'),
    ('declare -A m; m[one]=1; m["two words"]=2; echo "${m[one]}" "${m[two words]}" ${#m[@]}', '1 2 2\n'),
    ('declare -A m=([a]=1 [b]=2); for k in "${!m[@]}"; do echo "$k=${m[$k]}"; done | sort', 'a=1\nb=2\n'),
    ('declare -A c; for w in a b a; do (( c[$w]++ )); done; echo ${c[a]} ${c[b]}', '2 1\n'),
    ('declare -A m; k=key; m[$k]=v; [[ -n "${m[$k]+x}" ]] && echo has; [[ -z "${m[nope]+x}" ]] && echo not', 'has\nnot\n'),
    ("declare -A m=([a]=1); unset 'm[a]'; echo ${#m[@]}", '0\n'),
    ('IFS=, read -ra parts <<< \'a,b c,d\'; echo ${#parts[@]} "${parts[1]}"', '3 b c\n'),
    ('printf \'l1\\nl 2\\n\' > f; mapfile -t lines < f; echo ${#lines[@]} "${lines[1]}"', '2 l 2\n'),
    ('readarray -t lines < <(printf \'a\\nb\\n\'); echo "${lines[@]}"', 'a b\n'),
    ('a=(outer); f(){ local a=(inner more); echo ${#a[@]}; }; f; echo "${a[@]}"', '2\nouter\n'),
    ('f(){ local -a acc; acc+=(x); acc+=(y); echo "${acc[@]}"; }; f; echo "[${acc[@]}]"', 'x y\n[]\n'),
    ('a=(1); (a+=(2); echo ${#a[@]}); echo ${#a[@]}', '2\n1\n'),
    ('a=(1 2); echo "${a[@]}" | (read x y; echo $y); echo ${#a[@]}', '2\n2\n'),
    ('a=(\'x y\' z); IFS=,; echo "${a[*]}"; unset IFS', 'x y,z\n'),
    ('list=(3 1 2); IFS=$\'\\n\' sorted=($(sort <<<"${list[*]}")); unset IFS; echo "${sorted[@]}"', '1 2 3\n'),
    ('a=(a b c); unset \'a[0]\'; a=("${a[@]}"); echo "${!a[@]}"', '0 1\n'),
    ('args=(-l -a); set -- "${args[@]}" extra; echo $# "$2"', '3 -a\n'),
    ('f() { local -a r=("$@"); echo "${#r[@]}:${r[1]}"; }; f p \'q r\' s', '3:q r\n'),
    ('x=a; echo ${x/#/pre-} ${x/%/-post}', 'pre-a a-post\n'),
]
SECOND_MATCHESBASH = [
    ('source ~/.bashrc && echo ok', 'ok\n'),
    ('echo a; break; echo b', 'a\nb\n'),
    ('x=$(cat <<EOF\nit\'s fine\nEOF\n); echo "$x"', "it's fine\n"),
    ('x=$(cat <<EOF\n1) item\nEOF\n); echo "$x"', '1) item\n'),
    ('a=5 b=-3; echo $(($a-$b))', '8\n'),
    ('echo $((-2**2))', '4\n'),
    ("alias cnt='echo a b | wc -w'; cnt", '2\n'),
    ('x=outer; f(){ local x; echo "[$x]"; }; f', '[]\n'),
    ('set -- foo bar baz; echo "${@:2}"', 'bar baz\n'),
    ('echo x | exit 3; echo after', 'after\n'),
    ('false; $empty; echo $?', '0\n'),
    ('echo hi 3>/dev/null', 'hi\n'),
    ('touch a; sleep 0.02; touch b; [ b -nt a ] && [ a -ot b ] && echo newer', 'newer\n'),
    ('echo hi > f.txt; grep -r hi f.txt', 'hi\n'),
    ("mkdir d; echo hi > d/a.txt; echo hi > d/a.log; grep -r --include '*.txt' hi d", 'd/a.txt:hi\n'),
    ("echo foobar | grep -oP 'foo\\K\\w+'", 'bar\n'),
    ('awk \'BEGIN{while(i++<25000)printf "x";print ""}\' | cut -c5- | wc -c', '24997\n'),
]
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


@pytest.mark.parametrize("cmd,expected", ARRAY_CASES)
def test_arrays_match_bash(cmd, expected):
    r = run(cmd)
    assert r.stderr == ""
    assert r.stdout == expected


@pytest.mark.parametrize("cmd,expected", SECOND_MATCHESBASH)
def test_second_review_matches_bash(cmd, expected):
    assert run(cmd).stdout == expected


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
