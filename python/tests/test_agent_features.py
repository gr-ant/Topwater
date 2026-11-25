"""Features agents reach for most: control flow, expansions, awk, diff and everyday utilities (same cases as the C# suite)."""
import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import Shell  # noqa: E402


def out(shell: Shell, cmd: str) -> str:
    r = shell.run(cmd)
    assert r.exit_code == 0, f"`{cmd}` exited {r.exit_code}: {r.stderr}"
    return r.stdout


LANGUAGE_CASES = [
    # control flow
    ("if [ -d /tmp ]; then echo yes; else echo no; fi", "yes\n"),
    ("x=3; if (( x > 5 )); then echo big; elif (( x > 1 )); then echo mid; else echo small; fi", "mid\n"),
    ('for f in a b c; do echo "item $f"; done', "item a\nitem b\nitem c\n"),
    ("for i in {1..3}; do echo -n $i; done; echo", "123\n"),
    ("for ((i=0; i<3; i++)); do echo $i; done", "0\n1\n2\n"),
    ("i=0; while [ $i -lt 3 ]; do i=$((i+1)); done; echo $i", "3\n"),
    ("n=0; until [ $n -ge 2 ]; do ((n++)); done; echo $n", "2\n"),
    ("for i in 1 2 3 4; do [ $i = 3 ] && break; echo $i; done", "1\n2\n"),
    ("for i in 1 2 3; do [ $i = 2 ] && continue; echo $i; done", "1\n3\n"),
    ("case foo.txt in *.md) echo md;; *.txt|*.log) echo text;; *) echo other;; esac", "text\n"),
    ('greet() { echo "hi $1, $# args"; }; greet bob x', "hi bob, 2 args\n"),
    ("function f { local v=inner; echo $v; }; v=outer; f; echo $v", "inner\nouter\n"),
    ("f() { return 3; }; f; echo $?", "3\n"),
    ('sum() { echo $(( $1 + $2 )); }; echo "total: $(sum 2 5)"', "total: 7\n"),
    ("printf 'a\\nb\\nc\\n' | while read line; do echo \"<$line>\"; done", "<a>\n<b>\n<c>\n"),
    ("echo 'k1,v1' | while IFS=, read -r k v; do echo \"$k=$v\"; done", "k1=v1\n"),
    ("[[ -n $HOME && $HOME == /home/* ]] && echo match", "match\n"),
    ("v=abc123; [[ $v =~ ^[a-z]+[0-9]+$ ]] && echo ok", "ok\n"),
    ("f=report.txt; [[ $f != *.md ]] && echo notmd", "notmd\n"),
    # arithmetic
    ("echo $((2 + 3 * 4)) $(( (2+3)*4 )) $((7 / 2)) $((7 % 3)) $((2 ** 10))", "14 20 3 1 1024\n"),
    ("a=5; b=3; echo $((a > b ? a : b)) $((a == 5 && b == 3))", "5 1\n"),
    ("c=0; ((c++)); ((c += 10)); let c=c*2; echo $c", "22\n"),
    ("printf 'a\\nb\\nc\\n' > f.txt; echo $(( $(wc -l < f.txt) * 2 ))", "6\n"),
    ("fact() { if [ $1 -le 1 ]; then echo 1; else echo $(( $1 * $(fact $(($1-1))) )); fi; }; fact 5", "120\n"),
    # parameter expansion
    ("f=/var/log/app.tar.gz; echo ${f##*/} ${f%.*} ${f%%.*} ${f#*.}", "app.tar.gz /var/log/app.tar /var/log/app tar.gz\n"),
    ("s=hello; echo ${#s} ${s^^} ${s:1:3} ${s: -2} ${s/l/L} ${s//l/L}", "5 HELLO ell lo heLlo heLLo\n"),
    ("echo ${UNSET:-fallback} ${HOME:+set} [${UNSET:+x}]", "fallback set []\n"),
    ("echo {a,b}.txt pre{1..3} {x..z}", "a.txt b.txt pre1 pre2 pre3 x y z\n"),
    ("echo file{01..03}", "file01 file02 file03\n"),
    # set -e / exit / scripts
    ("set -e; false || echo recovered; echo after", "recovered\nafter\n"),
    ("set -o pipefail; false | true; echo $?", "1\n"),
    ("echo before; exit 0; echo after", "before\n"),
    ("command -v grep && command -v nosuch; echo $?", "/usr/bin/grep\n1\n"),
    ("eval 'x=42'; echo $x", "42\n"),
]


class AgentFeatureTests(unittest.TestCase):
    def test_shell_language(self):
        for cmd, expected in LANGUAGE_CASES:
            with self.subTest(cmd=cmd):
                self.assertEqual(expected, out(Shell(), cmd))

    def test_scripts_run_with_arguments(self):
        sh = Shell()
        out(sh, "cat > run.sh <<'EOF'\n#!/bin/bash\nset -e\necho \"script $1 $2 ($#)\"\nfor a in \"$@\"; do echo \"arg: $a\"; done\nEOF\nchmod +x run.sh")
        self.assertEqual("script x y z (2)\narg: x\narg: y z\n", out(sh, "./run.sh x 'y z'"))
        self.assertEqual("script a  (1)\narg: a\n", out(sh, "bash run.sh a"))
        out(sh, "printf '#!/usr/bin/env python3\\nprint(1)\\n' > p.py && chmod +x p.py")
        r = sh.run("./p.py")
        self.assertEqual(126, r.exit_code)
        self.assertIn("bad interpreter", r.stderr)
        self.assertEqual(127, sh.run("./missing.sh").exit_code)
        self.assertEqual(2, sh.run("exit 2").exit_code)
        e = sh.run("set -e; echo one; false; echo two")
        self.assertEqual(("one\n", 1), (e.stdout, e.exit_code))

    def test_awk(self):
        sh = Shell()
        sh.fs.write_text("/home/agent/data.csv", "name,dept,salary\nann,eng,120\nbob,ops,90\ncat,eng,100\n")
        self.assertEqual("ann\nbob\ncat\n", out(sh, "awk -F, 'NR>1 {print $1}' data.csv"))
        self.assertEqual("310\n", out(sh, "awk -F, 'NR>1 {s+=$3} END {print s}' data.csv"))
        self.assertEqual("eng 220\nops 90\n", out(sh, "awk -F, 'NR>1 {t[$2]+=$3} END {for (d in t) print d, t[d]}' data.csv | sort"))
        self.assertEqual("ann:120.00\n", out(sh, "awk -F, 'NR > 1 && $3 > 100 {printf \"%s:%.2f\\n\", $1, $3}' data.csv"))
        self.assertEqual("4\n", out(sh, "awk 'END {print NR}' data.csv"))
        self.assertEqual("bob\n", out(sh, "awk -F, '/ops/ {print $1}' data.csv"))
        self.assertEqual("a\nb\n", out(sh, "printf 'a\\nb\\na\\nb\\n' | awk '!seen[$0]++'"))
        self.assertEqual("3 c\n", out(sh, "echo 'a b c' | awk '{print NF, $NF}'"))
        self.assertEqual("X-b\n", out(sh, "echo 'a-b' | awk '{sub(/a/, \"X\"); print}'"))
        self.assertEqual("3 HELLO ell\n", out(sh, "echo hello | awk '{n=gsub(/l/, \"L\"); print n-1+2, toupper(\"hello\"), substr(\"hello\", 2, 3)}'"))
        self.assertEqual("1-2-3\n", out(sh, "echo '1 2 3' | awk -v OFS=- '{$1=$1; print}'"))
        self.assertEqual("x=5\n", out(sh, "awk -v n=5 'BEGIN {print \"x=\" n}'"))
        self.assertEqual("2\n", out(sh, "awk 'BEGIN {n = split(\"a:b\", parts, \":\"); print n}'"))
        self.assertEqual("big\nsmall\n", out(sh, "printf '10\\n2\\n' | awk '{if ($1 > 5) print \"big\"; else print \"small\"}'"))
        self.assertEqual("line2\nline3\n", out(sh, "printf 'line1\\nline2\\nline3\\nline4\\n' | awk '/line2/,/line3/'"))
        self.assertEqual("0.333333\n", out(sh, "awk 'BEGIN {print 1/3}'"))
        out(sh, "awk -F, 'NR>1 {print $1 > \"names.txt\"}' data.csv")
        self.assertEqual("ann\nbob\ncat\n", sh.fs.read_text("/home/agent/names.txt"))

    def test_diff_and_utilities(self):
        sh = Shell()
        sh.fs.write_text("/home/agent/a.txt", "one\ntwo\nthree\n")
        sh.fs.write_text("/home/agent/b.txt", "one\nTWO\nthree\nfour\n")
        r = sh.run("diff a.txt b.txt")
        self.assertEqual(1, r.exit_code)
        self.assertEqual("2c2\n< two\n---\n> TWO\n3a4\n> four\n", r.stdout)
        r = sh.run("diff -u a.txt b.txt")
        self.assertEqual("--- a.txt\n+++ b.txt\n@@ -1,3 +1,4 @@\n one\n-two\n+TWO\n three\n+four\n", r.stdout)
        self.assertEqual("", out(sh, "diff a.txt a.txt"))
        self.assertEqual("app.tar.gz\n/var/log\nreport\n", out(sh, "basename /var/log/app.tar.gz; dirname /var/log/app.tar.gz; basename report.txt .txt"))
        self.assertEqual("/home/agent/a.txt\n", out(sh, "realpath a.txt"))
        self.assertEqual("1\n2\n3\n", out(sh, "seq 3"))
        self.assertEqual("0,5,10\n", out(sh, "seq -s, 0 5 10"))
        self.assertEqual("14\n", out(sh, "stat -c %s a.txt"))
        self.assertEqual("a.txt: ASCII text\n", out(sh, "file a.txt"))
        self.assertEqual("three\ntwo\none\n", out(sh, "tac a.txt"))
        self.assertEqual("olleh\n", out(sh, "echo hello | rev"))
        self.assertEqual("one,two,three\n", out(sh, "paste -sd, a.txt"))
        self.assertEqual("     1\tone\n     2\ttwo\n     3\tthree\n", out(sh, "nl a.txt"))
        self.assertEqual("aGVsbG8=\nhello", out(sh, "echo -n hello | base64; echo aGVsbG8= | base64 -d"))
        self.assertEqual("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824  -\n", out(sh, "echo -n hello | sha256sum"))
        self.assertEqual("a    bb\nccc  d\n", out(sh, "printf 'a bb\\nccc d\\n' | column -t"))
        self.assertTrue(out(sh, "mktemp").startswith("/tmp/tmp."))
        self.assertIn("ID=ubuntu", out(sh, "cat /etc/os-release"))
        self.assertIn("grep\n", out(sh, "ls /usr/bin"))
        self.assertEqual("", out(sh, "cat /dev/null; echo hi > /dev/null"))
        self.assertEqual("uid=1000(agent) gid=1000(agent) groups=1000(agent)\n4\n", out(sh, "id; nproc"))
        self.assertEqual(124, sh.run("timeout 0.05 sleep 5").exit_code)
        self.assertTrue(out(sh, "awk --version").startswith("GNU Awk"))
        self.assertEqual("a.txt\nb.txt\n", out(sh, "find . -mmin -5 -name '*.txt' | sort | sed 's|./||'"))
        self.assertEqual("", out(sh, "grep -rIl ELF /usr/bin; ls -d /var/tmp /opt >/dev/null"))

    def test_gaps_found_by_real_model(self):
        """Gaps found by running Gemini against the shell (examples/gemini_harness.py)."""
        sh = Shell()
        out(sh, "mkdir -p project/src && touch project/src/a.py project/src/b.py project/README.md")
        # the script Gemini wrote: read -d '' + find -print0 + process substitution
        out(sh, "cat << 'EOF' > ~/backup.sh\n#!/bin/bash\nset -e\nBACKUP_DIR=\"$HOME/backup\"\nmkdir -p \"$BACKUP_DIR\"\ncount=0\n"
                "while IFS= read -r -d '' file; do\n    cp \"$file\" \"$BACKUP_DIR/\"\n    count=$((count + 1))\n"
                "done < <(find \"$HOME/project\" -type f -name \"*.py\" -print0)\necho \"$count\"\nEOF\nchmod +x ~/backup.sh")
        self.assertEqual("2\n", out(sh, "~/backup.sh"))
        self.assertEqual("a.py\nb.py\n", out(sh, "ls backup"))
        self.assertEqual("", out(sh, "ls /dev/fd"))  # process-substitution files are cleaned up
        self.assertEqual("project/README.md\nproject/src/a.py\nproject/src/b.py\n", out(sh, "find project -name '*.md' -o -name '*.py' | sort"))
        self.assertEqual("project/src\n", out(sh, "find project -type d ! -name project"))
        self.assertEqual("x\nx\n", out(sh, "echo x > project/src/a.py; echo x > project/src/b.py; find project -name '*.py' -exec cat {} \\;"))
        self.assertEqual("x\nx\n", out(sh, "find project -name '*.py' -exec cat {} +"))
        out(sh, "find project -name '*.md' -delete")
        self.assertFalse(sh.fs.exists("/home/agent/project/README.md"))
        self.assertEqual("project:\nsrc\n\nproject/src:\na.py\nb.py\n", out(sh, "ls -R project"))
        self.assertEqual("sort (GNU coreutils) 9.4\n", out(sh, "sort --version"))
        self.assertTrue(out(sh, "bash --version").startswith("GNU bash, version 5"))
        self.assertEqual("file\n", out(sh, "type -t ls"))
        self.assertIn("/bin/sh", out(sh, "ls /bin/sh /etc/issue"))
        self.assertEqual("a|b\n", out(sh, "printf 'a,b' | { IFS= read -r -d , x; read -r y; echo \"$x|$y\"; }"))
        self.assertEqual("3\n", out(sh, "wc -l < <(printf '1\\n2\\n3\\n')"))
        self.assertEqual("north 220\nsouth 100\neast 42\n", out(sh, "printf 'south 100\\neast 42\\nnorth 220\\n' | sort -k2,2nr"))
        self.assertEqual("b,2,x\na,10,y\n", out(sh, "printf 'a,10,y\\nb,2,x\\n' | sort -t, -k2n --debug"))
        self.assertEqual("project/src/a.py:x\n", out(sh, "mkdir -p project/node_modules; echo x > project/node_modules/z.py; echo x > project/n.txt; grep -r --include='*.py' --exclude=b.py --exclude-dir={node_modules,.git} x project"))

    def test_regular_user_permissions(self):
        """The agent is a regular, non-root user."""
        sh = Shell()
        # chmod on the agent's own files is real and visible
        out(sh, "printf 'echo ran' > s.sh")
        r = sh.run("./s.sh")
        self.assertEqual((126, "bash: ./s.sh: Permission denied\n"), (r.exit_code, r.stderr))
        self.assertEqual("-rw-r--r--\n-rwxr-xr-x\nran\n", out(sh, "ls -l s.sh | cut -c1-10; chmod +x s.sh; ls -l s.sh | cut -c1-10; ./s.sh"))
        self.assertEqual("mode of 's.sh' changed from 0755 (rwxr-xr-x) to 0700 (rwx------)\n", out(sh, "chmod -v go-rx s.sh"))
        self.assertEqual("640 agent\n", out(sh, "chmod 640 s.sh; stat -c '%a %U' s.sh"))
        self.assertEqual("ok\n", out(sh, "[ -x /usr/bin/grep ] && [ ! -w /etc/passwd ] && [ -w ~ ] && echo ok"))
        # system areas belong to root
        for cmd, err in [
            ("echo x > /etc/motd", "bash: /etc/motd: Permission denied\n"),
            ("rm /usr/bin/grep", "rm: cannot remove '/usr/bin/grep': Permission denied\n"),
            ("chmod 777 /etc/passwd", "chmod: changing permissions of '/etc/passwd': Operation not permitted\n"),
            ("chown agent /etc/passwd", "chown: changing ownership of '/etc/passwd': Operation not permitted\n"),
            ("ls /root", "ls: cannot open directory '/root': Permission denied\n"),
            ("sudo cat /etc/shadow", "agent is not in the sudoers file.\n"),
        ]:
            r = sh.run(cmd)
            self.assertTrue(r.exit_code != 0 and r.stderr == err, f"{cmd}: [{r.exit_code}] {r.stderr}")
        self.assertTrue(sh.fs.is_file("/usr/bin/grep"))
        self.assertIn("root:x:0:0", out(sh, "cat /etc/passwd"))
        self.assertEqual("drwxrwxrwt root\n", out(sh, "stat -c '%A %U' /tmp"))
        self.assertEqual("x\n", out(sh, "echo x > /tmp/f && cat /tmp/f"))
        r = sh.run("find / -name shadow")
        self.assertEqual((1, "find: '/root': Permission denied\n"), (r.exit_code, r.stderr))
        # the app itself is unrestricted, and files it creates belong to the directory's owner
        sh.fs.write_text("/etc/app.conf", "mode=prod")
        sh.fs.write_text("/home/agent/notes.txt", "hi")
        self.assertEqual("root\nagent\n", out(sh, "stat -c %U /etc/app.conf ~/notes.txt"))
        self.assertEqual("hi!\n", out(sh, "echo '!' >> ~/notes.txt; tr -d '\\n' < ~/notes.txt; echo"))

    def test_runaway_loops_are_stopped(self):
        r = Shell(max_loop_iterations=50).run("while true; do :; done")
        self.assertNotEqual(0, r.exit_code)
        self.assertIn("loop aborted", r.stderr)


if __name__ == "__main__":
    unittest.main()
