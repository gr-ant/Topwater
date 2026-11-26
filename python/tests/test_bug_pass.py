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
