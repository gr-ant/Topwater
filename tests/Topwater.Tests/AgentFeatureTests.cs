using Topwater;

namespace Topwater.Tests;

/// <summary>Features agents reach for most: control flow, expansions, awk, diff and everyday utilities.</summary>
public class AgentFeatureTests
{
    static async Task<string> Out(Shell shell, string cmd)
    {
        var r = await shell.RunAsync(cmd);
        Assert.True(r.ExitCode == 0, $"`{cmd}` exited {r.ExitCode}: {r.Stderr}");
        return r.Stdout;
    }

    [Theory]
    // control flow
    [InlineData("if [ -d /tmp ]; then echo yes; else echo no; fi", "yes\n")]
    [InlineData("x=3; if (( x > 5 )); then echo big; elif (( x > 1 )); then echo mid; else echo small; fi", "mid\n")]
    [InlineData("for f in a b c; do echo \"item $f\"; done", "item a\nitem b\nitem c\n")]
    [InlineData("for i in {1..3}; do echo -n $i; done; echo", "123\n")]
    [InlineData("for ((i=0; i<3; i++)); do echo $i; done", "0\n1\n2\n")]
    [InlineData("i=0; while [ $i -lt 3 ]; do i=$((i+1)); done; echo $i", "3\n")]
    [InlineData("n=0; until [ $n -ge 2 ]; do ((n++)); done; echo $n", "2\n")]
    [InlineData("for i in 1 2 3 4; do [ $i = 3 ] && break; echo $i; done", "1\n2\n")]
    [InlineData("for i in 1 2 3; do [ $i = 2 ] && continue; echo $i; done", "1\n3\n")]
    [InlineData("case foo.txt in *.md) echo md;; *.txt|*.log) echo text;; *) echo other;; esac", "text\n")]
    [InlineData("greet() { echo \"hi $1, $# args\"; }; greet bob x", "hi bob, 2 args\n")]
    [InlineData("function f { local v=inner; echo $v; }; v=outer; f; echo $v", "inner\nouter\n")]
    [InlineData("f() { return 3; }; f; echo $?", "3\n")]
    [InlineData("sum() { echo $(( $1 + $2 )); }; echo \"total: $(sum 2 5)\"", "total: 7\n")]
    [InlineData("printf 'a\\nb\\nc\\n' | while read line; do echo \"<$line>\"; done", "<a>\n<b>\n<c>\n")]
    [InlineData("echo 'k1,v1' | while IFS=, read -r k v; do echo \"$k=$v\"; done", "k1=v1\n")]
    [InlineData("[[ -n $HOME && $HOME == /home/* ]] && echo match", "match\n")]
    [InlineData("v=abc123; [[ $v =~ ^[a-z]+[0-9]+$ ]] && echo ok", "ok\n")]
    [InlineData("f=report.txt; [[ $f != *.md ]] && echo notmd", "notmd\n")]
    // arithmetic
    [InlineData("echo $((2 + 3 * 4)) $(( (2+3)*4 )) $((7 / 2)) $((7 % 3)) $((2 ** 10))", "14 20 3 1 1024\n")]
    [InlineData("a=5; b=3; echo $((a > b ? a : b)) $((a == 5 && b == 3))", "5 1\n")]
    [InlineData("c=0; ((c++)); ((c += 10)); let c=c*2; echo $c", "22\n")]
    [InlineData("printf 'a\\nb\\nc\\n' > f.txt; echo $(( $(wc -l < f.txt) * 2 ))", "6\n")]
    [InlineData("fact() { if [ $1 -le 1 ]; then echo 1; else echo $(( $1 * $(fact $(($1-1))) )); fi; }; fact 5", "120\n")]
    // parameter expansion
    [InlineData("f=/var/log/app.tar.gz; echo ${f##*/} ${f%.*} ${f%%.*} ${f#*.}", "app.tar.gz /var/log/app.tar /var/log/app tar.gz\n")]
    [InlineData("s=hello; echo ${#s} ${s^^} ${s:1:3} ${s: -2} ${s/l/L} ${s//l/L}", "5 HELLO ell lo heLlo heLLo\n")]
    [InlineData("echo ${UNSET:-fallback} ${HOME:+set} [${UNSET:+x}]", "fallback set []\n")]
    [InlineData("echo {a,b}.txt pre{1..3} {x..z}", "a.txt b.txt pre1 pre2 pre3 x y z\n")]
    [InlineData("echo file{01..03}", "file01 file02 file03\n")]
    // set -e / exit / scripts
    [InlineData("set -e; false || echo recovered; echo after", "recovered\nafter\n")]
    [InlineData("set -o pipefail; false | true; echo $?", "1\n")]
    [InlineData("echo before; exit 0; echo after", "before\n")]
    [InlineData("command -v grep && command -v nosuch; echo $?", "/usr/bin/grep\n1\n")]
    [InlineData("eval 'x=42'; echo $x", "42\n")]
    public async Task ShellLanguage(string cmd, string expected) => Assert.Equal(expected, await Out(new Shell(), cmd));

    [Fact]
    public async Task ScriptsRunWithArguments()
    {
        var sh = new Shell();
        await Out(sh, "cat > run.sh <<'EOF'\n#!/bin/bash\nset -e\necho \"script $1 $2 ($#)\"\nfor a in \"$@\"; do echo \"arg: $a\"; done\nEOF\nchmod +x run.sh");
        Assert.Equal("script x y z (2)\narg: x\narg: y z\n", await Out(sh, "./run.sh x 'y z'"));
        Assert.Equal("script a  (1)\narg: a\n", await Out(sh, "bash run.sh a"));
        await Out(sh, "printf '#!/usr/bin/env python3\\nprint(1)\\n' > p.py && chmod +x p.py");
        var r = await sh.RunAsync("./p.py");
        Assert.Equal(126, r.ExitCode);
        Assert.Contains("bad interpreter", r.Stderr);
        Assert.Equal(127, (await sh.RunAsync("./missing.sh")).ExitCode);
        Assert.Equal(2, (await sh.RunAsync("exit 2")).ExitCode);
        var e = await sh.RunAsync("set -e; echo one; false; echo two");
        Assert.Equal(("one\n", 1), (e.Stdout, e.ExitCode));
    }

    [Fact]
    public async Task Awk()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/home/agent/data.csv", "name,dept,salary\nann,eng,120\nbob,ops,90\ncat,eng,100\n");
        Assert.Equal("ann\nbob\ncat\n", await Out(sh, "awk -F, 'NR>1 {print $1}' data.csv"));
        Assert.Equal("310\n", await Out(sh, "awk -F, 'NR>1 {s+=$3} END {print s}' data.csv"));
        Assert.Equal("eng 220\nops 90\n", await Out(sh, "awk -F, 'NR>1 {t[$2]+=$3} END {for (d in t) print d, t[d]}' data.csv | sort"));
        Assert.Equal("ann:120.00\n", await Out(sh, "awk -F, 'NR > 1 && $3 > 100 {printf \"%s:%.2f\\n\", $1, $3}' data.csv"));
        Assert.Equal("4\n", await Out(sh, "awk 'END {print NR}' data.csv"));
        Assert.Equal("bob\n", await Out(sh, "awk -F, '/ops/ {print $1}' data.csv"));
        Assert.Equal("a\nb\n", await Out(sh, "printf 'a\\nb\\na\\nb\\n' | awk '!seen[$0]++'"));
        Assert.Equal("3 c\n", await Out(sh, "echo 'a b c' | awk '{print NF, $NF}'"));
        Assert.Equal("X-b\n", await Out(sh, "echo 'a-b' | awk '{sub(/a/, \"X\"); print}'"));
        Assert.Equal("3 HELLO ell\n", await Out(sh, "echo hello | awk '{n=gsub(/l/, \"L\"); print n-1+2, toupper(\"hello\"), substr(\"hello\", 2, 3)}'"));
        Assert.Equal("1-2-3\n", await Out(sh, "echo '1 2 3' | awk -v OFS=- '{$1=$1; print}'"));
        Assert.Equal("x=5\n", await Out(sh, "awk -v n=5 'BEGIN {print \"x=\" n}'"));
        Assert.Equal("2\n", await Out(sh, "awk 'BEGIN {n = split(\"a:b\", parts, \":\"); print n}'"));
        Assert.Equal("big\nsmall\n", await Out(sh, "printf '10\\n2\\n' | awk '{if ($1 > 5) print \"big\"; else print \"small\"}'"));
        Assert.Equal("line2\nline3\n", await Out(sh, "printf 'line1\\nline2\\nline3\\nline4\\n' | awk '/line2/,/line3/'"));
        Assert.Equal("0.333333\n", await Out(sh, "awk 'BEGIN {print 1/3}'"));
        await Out(sh, "awk -F, 'NR>1 {print $1 > \"names.txt\"}' data.csv");
        Assert.Equal("ann\nbob\ncat\n", sh.Fs.ReadAllText("/home/agent/names.txt"));
    }

    [Fact]
    public async Task DiffAndUtilities()
    {
        var sh = new Shell();
        sh.Fs.WriteAllText("/home/agent/a.txt", "one\ntwo\nthree\n");
        sh.Fs.WriteAllText("/home/agent/b.txt", "one\nTWO\nthree\nfour\n");
        var r = await sh.RunAsync("diff a.txt b.txt");
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("2c2\n< two\n---\n> TWO\n3a4\n> four\n", r.Stdout);
        r = await sh.RunAsync("diff -u a.txt b.txt");
        Assert.Equal("--- a.txt\n+++ b.txt\n@@ -1,3 +1,4 @@\n one\n-two\n+TWO\n three\n+four\n", r.Stdout);
        Assert.Equal("", await Out(sh, "diff a.txt a.txt"));

        Assert.Equal("app.tar.gz\n/var/log\nreport\n", await Out(sh, "basename /var/log/app.tar.gz; dirname /var/log/app.tar.gz; basename report.txt .txt"));
        Assert.Equal("/home/agent/a.txt\n", await Out(sh, "realpath a.txt"));
        Assert.Equal("1\n2\n3\n", await Out(sh, "seq 3"));
        Assert.Equal("0,5,10\n", await Out(sh, "seq -s, 0 5 10"));
        Assert.Equal("14\n", await Out(sh, "stat -c %s a.txt"));
        Assert.Equal("a.txt: ASCII text\n", await Out(sh, "file a.txt"));
        Assert.Equal("three\ntwo\none\n", await Out(sh, "tac a.txt"));
        Assert.Equal("olleh\n", await Out(sh, "echo hello | rev"));
        Assert.Equal("one,two,three\n", await Out(sh, "paste -sd, a.txt"));
        Assert.Equal("     1\tone\n     2\ttwo\n     3\tthree\n", await Out(sh, "nl a.txt"));
        Assert.Equal("aGVsbG8=\nhello", await Out(sh, "echo -n hello | base64; echo aGVsbG8= | base64 -d"));
        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824  -\n", await Out(sh, "echo -n hello | sha256sum"));
        Assert.Equal("a    bb\nccc  d\n", await Out(sh, "printf 'a bb\\nccc d\\n' | column -t"));
        Assert.StartsWith("/tmp/tmp.", await Out(sh, "mktemp"));
        Assert.Contains("ID=ubuntu", await Out(sh, "cat /etc/os-release"));
        Assert.Contains("grep\n", await Out(sh, "ls /usr/bin"));
        Assert.Equal("", await Out(sh, "cat /dev/null; echo hi > /dev/null"));
        Assert.Equal("uid=1000(agent) gid=1000(agent) groups=1000(agent)\n4\n", await Out(sh, "id; nproc"));
        Assert.Equal(124, (await sh.RunAsync("timeout 0.05 sleep 5")).ExitCode);
        Assert.StartsWith("GNU Awk", await Out(sh, "awk --version"));
        Assert.Equal("a.txt\nb.txt\n", await Out(sh, "find . -mmin -5 -name '*.txt' | sort | sed 's|./||'"));
        Assert.Equal("", await Out(sh, "grep -rIl ELF /usr/bin; ls -d /var/tmp /opt >/dev/null"));
    }

    /// <summary>Gaps found by running Gemini against the shell (examples/gemini_harness.py).</summary>
    [Fact]
    public async Task GapsFoundByRealModel()
    {
        var sh = new Shell();
        await Out(sh, "mkdir -p project/src && touch project/src/a.py project/src/b.py project/README.md");
        // the script Gemini wrote: read -d '' + find -print0 + process substitution
        await Out(sh, "cat << 'EOF' > ~/backup.sh\n#!/bin/bash\nset -e\nBACKUP_DIR=\"$HOME/backup\"\nmkdir -p \"$BACKUP_DIR\"\ncount=0\n" +
                      "while IFS= read -r -d '' file; do\n    cp \"$file\" \"$BACKUP_DIR/\"\n    count=$((count + 1))\n" +
                      "done < <(find \"$HOME/project\" -type f -name \"*.py\" -print0)\necho \"$count\"\nEOF\nchmod +x ~/backup.sh");
        Assert.Equal("2\n", await Out(sh, "~/backup.sh"));
        Assert.Equal("a.py\nb.py\n", await Out(sh, "ls backup"));
        Assert.Equal("", await Out(sh, "ls /dev/fd")); // process-substitution files are cleaned up

        Assert.Equal("project/README.md\nproject/src/a.py\nproject/src/b.py\n", await Out(sh, "find project -name '*.md' -o -name '*.py' | sort"));
        Assert.Equal("project/src\n", await Out(sh, "find project -type d ! -name project"));
        Assert.Equal("x\nx\n", await Out(sh, "echo x > project/src/a.py; echo x > project/src/b.py; find project -name '*.py' -exec cat {} \\;"));
        Assert.Equal("x\nx\n", await Out(sh, "find project -name '*.py' -exec cat {} +"));
        await Out(sh, "find project -name '*.md' -delete");
        Assert.False(sh.Fs.Exists("/home/agent/project/README.md"));

        Assert.Equal("project:\nsrc\n\nproject/src:\na.py\nb.py\n", await Out(sh, "ls -R project"));
        Assert.Equal("sort (GNU coreutils) 9.4\n", await Out(sh, "sort --version"));
        Assert.StartsWith("GNU bash, version 5", await Out(sh, "bash --version"));
        Assert.Equal("file\n", await Out(sh, "type -t ls"));
        Assert.Contains("/bin/sh", await Out(sh, "ls /bin/sh /etc/issue"));
        Assert.Equal("a|b\n", await Out(sh, "printf 'a,b' | { IFS= read -r -d , x; read -r y; echo \"$x|$y\"; }"));
        Assert.Equal("3\n", await Out(sh, "wc -l < <(printf '1\\n2\\n3\\n')"));
        Assert.Equal("north 220\nsouth 100\neast 42\n", await Out(sh, "printf 'south 100\\neast 42\\nnorth 220\\n' | sort -k2,2nr"));
        Assert.Equal("b,2,x\na,10,y\n", await Out(sh, "printf 'a,10,y\\nb,2,x\\n' | sort -t, -k2n --debug"));
        Assert.Equal("project/src/a.py:x\n", await Out(sh, "mkdir -p project/node_modules; echo x > project/node_modules/z.py; echo x > project/n.txt; grep -r --include='*.py' --exclude=b.py --exclude-dir={node_modules,.git} x project"));
    }

    /// <summary>The agent is a regular, non-root user.</summary>
    [Fact]
    public async Task RegularUserPermissions()
    {
        var sh = new Shell();
        // chmod on the agent's own files is real and visible
        await Out(sh, "printf 'echo ran' > s.sh");
        var r = await sh.RunAsync("./s.sh");
        Assert.Equal((126, "bash: ./s.sh: Permission denied\n"), (r.ExitCode, r.Stderr));
        Assert.Equal("-rw-r--r--\n-rwxr-xr-x\nran\n", await Out(sh, "ls -l s.sh | cut -c1-10; chmod +x s.sh; ls -l s.sh | cut -c1-10; ./s.sh"));
        Assert.Equal("mode of 's.sh' changed from 0755 (rwxr-xr-x) to 0700 (rwx------)\n", await Out(sh, "chmod -v go-rx s.sh"));
        Assert.Equal("640 agent\n", await Out(sh, "chmod 640 s.sh; stat -c '%a %U' s.sh"));
        Assert.Equal("ok\n", await Out(sh, "[ -x /usr/bin/grep ] && [ ! -w /etc/passwd ] && [ -w ~ ] && echo ok"));

        // system areas belong to root
        foreach (var (cmd, err) in new[]
        {
            ("echo x > /etc/motd", "bash: /etc/motd: Permission denied\n"),
            ("rm /usr/bin/grep", "rm: /usr/bin/grep: Permission denied\n"),
            ("chmod 777 /etc/passwd", "chmod: changing permissions of '/etc/passwd': Operation not permitted\n"),
            ("chown agent /etc/passwd", "chown: changing ownership of '/etc/passwd': Operation not permitted\n"),
            ("ls /root", "ls: /root: Permission denied\n"),
            ("sudo cat /etc/shadow", "agent is not in the sudoers file.\n"),
        })
        {
            r = await sh.RunAsync(cmd);
            Assert.True(r.ExitCode != 0 && r.Stderr == err, $"{cmd}: [{r.ExitCode}] {r.Stderr}");
        }
        Assert.True(sh.Fs.IsFile("/usr/bin/grep"));
        Assert.Contains("root:x:0:0", await Out(sh, "cat /etc/passwd"));               // still readable
        Assert.Equal("drwxrwxrwt root\n", await Out(sh, "stat -c '%A %U' /tmp"));      // shared scratch space
        Assert.Equal("x\n", await Out(sh, "echo x > /tmp/f && cat /tmp/f"));
        r = await sh.RunAsync("find / -name shadow");                                  // unreadable dirs are reported, not fatal
        Assert.Equal((1, "find: '/root': Permission denied\n"), (r.ExitCode, r.Stderr));

        // the app itself is unrestricted, and files it creates belong to the directory's owner
        sh.Fs.WriteAllText("/etc/app.conf", "mode=prod");
        sh.Fs.WriteAllText("/home/agent/notes.txt", "hi");
        Assert.Equal("root\nagent\n", await Out(sh, "stat -c %U /etc/app.conf ~/notes.txt"));
        Assert.Equal("hi!\n", await Out(sh, "echo '!' >> ~/notes.txt; tr -d '\\n' < ~/notes.txt; echo"));
    }

    [Fact]
    public async Task RunawayLoopsAreStopped()
    {
        var sh = new Shell(new ShellOptions { MaxLoopIterations = 50 });
        var r = await sh.RunAsync("while true; do :; done");
        Assert.NotEqual(0, r.ExitCode);
        Assert.Contains("loop aborted", r.Stderr);
    }
}

/// <summary>Details a model checks when it is deciding whether the machine is real.</summary>
public class RealismTests
{
    [Fact]
    public async Task ShellBuiltinsAreNotProgramFilesAndSystemFilesAreOld()
    {
        var sh = new Shell().Register("orders", _ => 0);
        var bin = (await sh.RunAsync("ls /bin")).Stdout.Split('\n');
        foreach (var name in new[] { "eval", "cd", "export", "break", "source", "history" }) Assert.DoesNotContain(name, bin);
        foreach (var name in new[] { "ls", "grep", "test", "echo", "orders" }) Assert.Contains(name, bin);
        Assert.Equal("ok\n", (await sh.RunAsync("eval 'echo ok'; cd /tmp && export X=1 && type cd >/dev/null")).Stdout);

        // nothing outside the agent's own areas looks recently modified
        await sh.RunAsync("echo hi > ~/new.txt");
        Assert.Equal("/home/agent\n/home/agent/new.txt\n", (await sh.RunAsync("find / -mmin -120 -not -path '/tmp*' -not -path '/var/tmp*' -not -path '/proc*' -not -path '/sys' -not -path '/run' 2>/dev/null | sort")).Stdout);
    }
}

public class LivedInMachineTests
{
    [Fact]
    public async Task TheMachineHasTheUsualClutter()
    {
        var sh = new Shell().Register("orders", _ => 0);
        async Task<string> Out(string c) => (await sh.RunAsync(c)).Stdout;

        Assert.Contains("HISTSIZE=1000", await Out("cat ~/.bashrc"));
        Assert.Equal(".bash_logout\n.bashrc\n.profile\n", await Out("ls -A ~"));
        Assert.Equal("", await Out("ls ~"));
        foreach (var dir in new[] { "proc", "sys", "lib", "sbin", "run", "boot" }) Assert.Contains(dir, (await Out("ls /")).Split('\n'));
        Assert.Equal("4\n", await Out("grep -c ^processor /proc/cpuinfo"));
        Assert.Equal(await Out("nproc"), await Out("grep -c ^processor /proc/cpuinfo"));
        Assert.Contains("8014880", await Out("grep MemTotal /proc/meminfo"));
        Assert.Contains("topwater", await Out("cat /etc/hosts"));
        Assert.Contains("agent:x:1000:", await Out("cat /etc/group"));

        // programs are not all the same suspicious 16 bytes
        long grep = long.Parse(await Out("stat -c %s /usr/bin/grep")), orders = long.Parse(await Out("stat -c %s /usr/bin/orders"));
        Assert.True(grep > 10_000 && orders > 10_000 && grep != orders);
    }
}
