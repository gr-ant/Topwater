"""Resource limits, ported from main's LimitTests: every exhaustion recipe ends in bounded time with its message, and the
shell stays usable."""
import re
import time

import pytest

from bashcompat import Shell


def quick(seconds: float = 1) -> Shell:
    return Shell(max_call_duration=seconds)


def hits_time_limit(sh: Shell, cmd: str, seconds: float = 1):
    """Runs a call that must hit the time limit; checks exit 124, the trailer, the bound, and that the next call works."""
    start = time.monotonic()
    r = sh.run(cmd)
    assert time.monotonic() - start < seconds + 8
    assert r.exit_code == 124
    secs = f"{seconds:g}"
    assert r.to_tool_output().endswith(
        f"[stopped: this call exceeded the {secs} s time limit; split the work into shorter calls. Running it again will hit the "
        "same limit. Files and variables changed before this point are kept.]")
    assert sh.run("echo ok").stdout == "ok\n"
    return r


def test_sleep_forever_stops_at_the_call_limit_and_keeps_earlier_output():
    sh = quick()
    r = hits_time_limit(sh, "x=kept; echo before; sleep 99999 2>/dev/null")
    assert r.to_tool_output().startswith("before\n")
    assert sh.run("echo $x").stdout == "kept\n"


def test_configured_duration_is_printed():
    hits_time_limit(quick(1.5), "sleep 60", 1.5)


def test_shell_loop_of_awk_runs_stops_at_the_call_limit():
    hits_time_limit(quick(), "while :; do awk 'BEGIN { while (1) x++ }' 2>/dev/null; done")


def test_slow_regex_over_many_lines_stops_at_the_call_limit():
    hits_time_limit(quick(), "x=$(printf 'a%.0s' {1..28})b; for i in {1..200}; do echo $x; done > s.txt; grep -cE '(a|aa)+$' s.txt 2>/dev/null")


@pytest.mark.parametrize("loop", [
    "awk 'BEGIN { while (1) x++ }'",
    "sed ':a; s/^/x/; ba' <<< x",  # the pattern space grows, so each step is slower than the last
    "jq -n 'last(range(1e12))'",
])
def test_timeout_interrupts_a_built_in_loop(loop):
    start = time.monotonic()
    r = Shell().run(f"timeout 1 {loop}; echo $?")
    assert time.monotonic() - start < 6
    assert r.stdout == "124\n"


def test_loop_limit_names_which_limit_tripped():
    sh = Shell(max_loop_iterations=10)
    one = sh.run("while :; do :; done 2>/dev/null")
    assert "loop aborted after 10 iterations (the limit for one loop)" in one.stderr
    assert one.to_tool_output().split("\n")[-1].startswith("[stopped: loop aborted after 10 iterations")
    all_ = sh.run("for i in {1..10}; do for j in {1..10}; do :; done; done")
    assert "loop aborted after 100 iterations in this call (the limit for all loops in one call)" in all_.stderr


# ---------- filesystem ----------

def small_disk() -> Shell:
    return Shell(max_file_system_bytes=1 << 20, max_loop_iterations=5_000)


KILOBYTE = "x=$(printf '%01023d' 0); "


def test_append_loop_stops_at_the_cap_and_rm_frees_space():
    sh = small_disk()
    r = sh.run(KILOBYTE + "for i in {1..2000}; do echo $x >> f.txt; done; echo done")
    assert "bash: /home/agent/f.txt: No space left on device" in r.stderr
    assert r.stdout.endswith("done\n")  # the call goes on
    assert sh.fs.used_bytes <= 1 << 20
    assert sh.run(KILOBYTE + "echo $x >> f.txt; echo $?").stdout == "1\n"
    assert re.search(r"/dev/sda1\s+1\.0M\s+1\.0M\s+\d+\s+100% /", sh.run("df -h .").stdout)
    assert sh.run("rm f.txt; echo hi > g.txt; echo $?").stdout == "0\n"
    assert re.search(r"/dev/sda1\s+1\.0M\s+\S+K\s+", sh.run("df -h .").stdout)


def test_unbounded_append_loop_ends_at_the_loop_limit():
    r = small_disk().run(KILOBYTE + "while :; do echo $x >> f.txt; done")
    assert "No space left on device" in r.stderr
    assert "loop aborted after 5000 iterations" in r.to_tool_output()


def test_cp_recursive_stops_at_the_cap():
    sh = small_disk()
    r = sh.run(KILOBYTE + "mkdir d; for i in {1..400}; do echo $x > d/$i; done; cp -r d d2; echo $?; cp -r d d3")
    assert r.stdout == "0\n"
    assert "cp: error writing 'd3': No space left on device" in r.stderr
    assert r.exit_code == 1
    assert not sh.fs.exists("/home/agent/d3")


def test_tar_extract_stops_at_the_cap():
    sh = small_disk()
    r = sh.run(KILOBYTE + "mkdir d; for i in {1..600}; do echo $x > d/$i; done; tar czf d.tgz d; rm -r d; mkdir a b; "
               "tar xzf d.tgz -C a; echo $?; tar xzf d.tgz -C b")
    assert r.stdout.startswith("0\n")
    assert "Cannot open: No space left on device" in r.stderr
    assert r.exit_code != 0
    assert sh.run("rm -r a b; tar xzf d.tgz -C . && echo ok").stdout == "ok\n"


def test_host_writes_are_counted_but_never_refused():
    sh = small_disk()
    sh.fs.write_text("/data.txt", "x" * (2 << 20))
    sh.fs.add_document("/docs/a.bin", bytes(100))
    assert sh.fs.used_bytes > 2 << 20
    r = sh.run("df .; echo y > y.txt")
    assert re.search(r"/dev/sda1\s+1024\s+20\d\d\s+0\s+", r.stdout)
    assert "No space left on device" in r.stderr


# ---------- strings ----------

def small_strings() -> Shell:
    return Shell(max_string_bytes=1 << 20)


def hits_string_limit(sh: Shell, cmd: str, message: str):
    """Runs a call that must hit the string limit: exit 1, the message on stderr and in the trailer, then a working shell."""
    start = time.monotonic()
    r = sh.run(cmd)
    assert time.monotonic() - start < 20
    assert r.exit_code == 1, r.stderr
    assert "bash: " + message in r.stderr
    last = r.to_tool_output().split("\n")[-1]
    assert last.startswith("[stopped: " + message)
    assert last.endswith("Running it again will hit the same limit. Files and variables changed before this point are kept.]")
    assert sh.run("echo ok").stdout == "ok\n"
    return r


def test_bash_string_doubling_stops_at_the_default_limit():
    hits_string_limit(Shell(), "x=a; while :; do x=$x$x; done 2>/dev/null", "variables in total too large (limit 32 MB)")


@pytest.mark.parametrize("cmd,message", [
    ("awk 'BEGIN { x = \"a\"; while (1) x = x x }' 2>/dev/null", "awk: string too large (limit 1 MB)"),
    ("awk 'BEGIN { x = \"ab\"; while (1) gsub(/./, \"&&\", x) }'", "awk: string too large (limit 1 MB)"),
    ("echo a | sed ':a; s/.*/&&/; ba' 2>/dev/null", "sed: pattern space too large (limit 1 MB)"),
    ("jq -n 'reduce range(100) as $i (\"a\"; . + .)' 2>/dev/null", "jq: value too large (limit 1 MB)"),
    ("jq -n 'reduce range(100) as $i ([1]; . + .)'", "jq: value too large (limit 1 MB)"),
    ("jq -n '\"ab\" * 1e12'", "jq: value too large (limit 1 MB)"),
    ("x=$(printf '%02000d' 0); y=${x//?/$x}", "expansion too large (limit 1 MB)"),
    ("m=$(printf '%01000d' 0); echo {1..10000}$m >/dev/null", "expansion too large (limit 1 MB)"),
    ("big=$(printf '%0100000d' 0); for ((i = 0; i < 1000000; i++)); do a[i]=$big; done", "variables in total too large (limit 1 MB)"),
    ("awk 'BEGIN { for (i = 0; i < 2100; i++) printf \"%01000d\", 0 }' > f; read -r x < f", "x: value too large (limit 1 MB)"),
    ("awk 'BEGIN { for (i = 0; i < 2100; i++) printf \"%01000d\", 0 }' > f; cat f 2>/dev/null", "output too large (limit 1 MB)"),
    ("awk 'BEGIN { for (i = 0; i < 2100; i++) printf \"%01000d\", 0 }' > f; cat f | wc -c", "output too large (limit 1 MB)"),
    ("awk 'BEGIN { for (i = 0; i < 2100; i++) printf \"%01000d\", 0 }' > f; x=$(cat f)", "output too large (limit 1 MB)"),
])
def test_string_recipes_stop_at_the_configured_limit(cmd, message):
    hits_string_limit(small_strings(), cmd, message)


def test_huge_printf_width_is_bounded():
    assert small_strings().run("printf '%0999999999d' 1 | wc -c").stdout == "10000\n"


def test_redirects_are_bounded_by_the_disk_not_the_string_limit():
    r = small_strings().run("awk 'BEGIN { for (i = 0; i < 3000; i++) printf \"%01000d\\n\", 0 }' > f; cat f f > g; wc -c < g")
    assert r.stdout == "6006000\n"
    assert r.exit_code == 0


def test_variables_can_be_rewritten_near_the_limit():
    # x is 600 KB: x=${x}y holds 1.2 MB for a moment but only 600 KB after, so it must not count both
    sh = small_strings()
    sh.run("kb() { awk -v n=$1 'BEGIN { for (i = 0; i < n; i++) printf \"%01000d\", 0 }'; }")
    r = sh.run("x=$(kb 600); for i in 1 2 3; do x=${x}y; done; echo ${#x}; unset x; y=$(kb 900); echo ${#y}")
    assert r.stdout == "600003\n900000\n"


def test_trailer_survives_the_output_caps():
    text = quick().run("seq 1 20000; seq 1 3000 >&2; sleep 99").to_tool_output()
    assert "[output truncated" in text
    assert text.endswith("Files and variables changed before this point are kept.]")
