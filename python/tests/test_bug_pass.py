"""Fixes found by the fidelity reviews (016-020, 023) that the shared corpus cannot record from real bash."""
from bashcompat import Shell


def test_touch_with_a_time_past_what_the_port_holds_is_an_invalid_date():
    # GNU touch takes year 3170843; a datetime cannot, so say what touch says for a bad date, never the internal error
    sh = Shell()
    r = sh.run("cd /tmp; touch -d @99999999999999 f; date -d @99999999999999")
    assert r.stdout == ""
    assert r.stderr == "touch: invalid date format ‘@99999999999999’\ndate: invalid date ‘@99999999999999’\n"
    assert sh.run("cd /tmp; ls f").exit_code == 2


def test_umask_applies_to_copies_and_is_per_shell():
    sh = Shell()
    r = sh.run("cd /tmp; touch a; chmod 666 a; umask 027; cp a b; cp -p a c; mkdir d; ls -ld b c d | cut -c1-10")
    assert r.stdout == "-rw-r-----\n-rw-rw-rw-\ndrwxr-x---\n"
    assert sh.run("umask").stdout == "0027\n"  # the mask lasts across calls
    assert Shell().run("umask").stdout == "0022\n"
    sh.fs.write_text("/tmp/app.txt", "x")  # the app's own files keep the default mode
    assert sh.run("ls -l /tmp/app.txt | cut -c1-10").stdout == "-rw-r--r--\n"


# The regex time limit (TimedPattern) sets SIGALRM only around a single match; the host's signals stay the host's.

def test_a_host_tool_runs_with_no_alarm_of_ours():
    import signal
    seen = []

    def tool(ctx):
        seen.append((signal.getsignal(signal.SIGALRM), signal.getitimer(signal.ITIMER_REAL)))
        return 0

    sh = Shell().register_command("probe", tool)
    assert sh.run("echo aaa | grep -cE '(a+)+$'; probe; echo ok").stdout == "1\nok\n"
    assert seen == [(signal.SIG_DFL, (0.0, 0.0))]


def test_the_hosts_handler_and_timer_survive_a_call():
    import signal
    fired = []
    previous = signal.signal(signal.SIGALRM, lambda *_: fired.append(1))
    try:
        signal.setitimer(signal.ITIMER_REAL, 30)
        handler = signal.getsignal(signal.SIGALRM)
        assert Shell().run("echo aaa | grep -cE '(a+)+$'; [[ aa =~ a+ ]]").exit_code == 0
        assert signal.getsignal(signal.SIGALRM) is handler
        assert 0 < signal.getitimer(signal.ITIMER_REAL)[0] <= 30
    finally:
        signal.setitimer(signal.ITIMER_REAL, 0)
        signal.signal(signal.SIGALRM, previous)
    assert fired == []


def test_two_shells_keep_their_own_limits():
    import asyncio
    slow_tool = Shell(max_call_duration=0.3)
    regex = Shell(max_call_duration=60)

    async def both():
        # the regex shell's match takes a few tenths of a second; the other shell's short limit must not stop it
        return await asyncio.gather(slow_tool.run_async("sleep 1"), regex.run_async(
            "printf 'a%.0s' {1..20} > s; echo b >> s; grep -cE '(a+)+$' s"))

    a, b = asyncio.run(both())
    assert a.exit_code == 124
    assert (b.stdout, b.exit_code) == ("0\n", 1)


def test_a_call_from_a_worker_thread_works_without_the_limit():
    import threading
    results = []
    t = threading.Thread(target=lambda: results.append(Shell().run("echo aaa | grep -cE '(a+)+$'")))
    t.start()
    t.join(30)
    assert results[0].stdout == "1\n"
