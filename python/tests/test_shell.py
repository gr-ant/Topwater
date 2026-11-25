import asyncio
import enum
import re
import sys
import unittest
from pathlib import Path
from typing import Annotated

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "src"))

from topwater import CommandContext, Shell, command, execute_tool  # noqa: E402


def out(shell: Shell, cmd: str) -> str:
    r = shell.run(cmd)
    assert r.exit_code == 0, f"`{cmd}` exited {r.exit_code}: {r.stderr}"
    return r.stdout


class SyntaxTests(unittest.TestCase):
    CASES = [
        ("echo hello world", "hello world\n"),
        ("echo 'single $HOME' \"double $USER\"", "single $HOME double agent\n"),
        ('echo a\\ b "c\\"d"', 'a b c"d\n'),
        ("echo -n hi", "hi"),
        ("echo -e 'a\\tb'", "a\tb\n"),
        ('X=5; echo $X ${X} "${MISSING:-dflt}"', "5 5 dflt\n"),
        ("echo $(echo inner) `echo tick`", "inner tick\n"),
        ("echo \"$(printf 'a  b')\"", "a  b\n"),
        ("true && echo yes || echo no", "yes\n"),
        ("false && echo yes || echo no", "no\n"),
        ("false; echo $?", "1\n"),
        ("! false && echo negated", "negated\n"),
        ("printf '%s=%03d\\n' a 7 b 42", "a=007\nb=042\n"),
        ("echo one # a comment", "one\n"),
        ("{ echo a; echo b; } | wc -l", "2\n"),
        ("(cd /tmp && pwd); pwd", "/tmp\n/home/agent\n"),
        ("echo ~ ~/x", "/home/agent /home/agent/x\n"),
        ("A=1 env | grep '^A='; echo \"[$A]\"", "A=1\n[]\n"),
        ("bash -c 'echo from bash'", "from bash\n"),
        ("[ 3 -gt 2 ] && test -d /tmp && echo ok", "ok\n"),
    ]

    def test_syntax(self):
        for cmd, expected in self.CASES:
            with self.subTest(cmd=cmd):
                self.assertEqual(expected, out(Shell(), cmd))


class ShellTests(unittest.TestCase):
    def test_filesystem_persists_across_calls(self):
        sh = Shell()
        out(sh, "mkdir -p proj/src && cd proj")
        out(sh, "echo 'line one' > src/a.txt && echo 'line two' >> src/a.txt")
        self.assertEqual("/home/agent/proj\n", out(sh, "pwd"))
        self.assertEqual("line one\nline two\n", out(sh, "cat src/a.txt"))
        out(sh, "cp src/a.txt src/b.txt && mv src/b.txt c.txt && touch src/z.md")
        self.assertEqual("c.txt\nsrc\n", out(sh, "ls"))
        self.assertEqual("src/a.txt\nsrc/z.md\n", out(sh, "ls src/*"))
        self.assertEqual("./src/a.txt\n", out(sh, "find . -name '*.txt' -path '*src*'"))
        self.assertEqual(".\n├── c.txt\n└── src\n    ├── a.txt\n    └── z.md\n\n1 directory, 3 files\n", out(sh, "tree"))
        out(sh, "rm -r src")
        self.assertFalse(sh.fs.exists("/home/agent/proj/src"))

    def test_heredocs_and_redirects(self):
        sh = Shell()
        out(sh, "NAME=world\ncat > hi.txt <<EOF\nhello $NAME\nEOF\ncat > raw.txt <<'EOF'\nhello $NAME\nEOF")
        self.assertEqual("hello world\n", sh.fs.read_text("/home/agent/hi.txt"))
        self.assertEqual("hello $NAME\n", sh.fs.read_text("/home/agent/raw.txt"))
        r = sh.run("cat nope 2>&1 > out.txt; cat missing > all.txt 2>&1")
        self.assertIn("No such file", r.stdout)
        self.assertIn("No such file", sh.fs.read_text("/home/agent/all.txt"))
        self.assertEqual("", out(sh, "cat missing 2>/dev/null; true"))
        self.assertEqual("1\n3\n", out(sh, "wc -l < hi.txt; wc -l <<< 'x' > /dev/null; printf 'a\\nb\\nc\\n' | wc -l"))

    def test_text_tools(self):
        sh = Shell()
        sh.fs.write_text("/home/agent/log.txt", "info start\nERROR disk full\ninfo retry\nerror again\ninfo done\n")
        self.assertEqual("2:ERROR disk full\n4:error again\n", out(sh, "grep -in error log.txt"))
        self.assertEqual("2\n", out(sh, "grep -ci 'error' log.txt"))
        self.assertEqual("info start\n--\ninfo retry\nerror again\n", out(sh, "grep -B1 -i 'start\\|again' log.txt | grep -v ERROR"))
        self.assertEqual("ERROR disk full\n", out(sh, "grep -E 'disk|nothing' log.txt"))
        self.assertEqual("./log.txt:3\n", out(sh, "grep -rc info . | grep -v '/\\.'"))
        self.assertEqual("info done\n", out(sh, "tail -1 log.txt"))
        self.assertEqual("info start\nERROR disk full\n", out(sh, "head -n 2 log.txt"))
        self.assertEqual("ERROR disk full\ninfo retry\n", out(sh, "sed -n '2,3p' log.txt"))
        self.assertEqual("INFO start\n", out(sh, "sed 's/info/INFO/g' log.txt | head -1"))
        self.assertEqual("[info] start\n", out(sh, "sed -E 's/^(\\w+) /[\\1] /' log.txt | head -1"))
        out(sh, "sed -i '/^info/d' log.txt")
        self.assertEqual("ERROR disk full\nerror again\n", out(sh, "cat log.txt"))
        self.assertEqual("      2 b\n      1 a\n", out(sh, "printf 'b\\na\\nb\\n' | sort | uniq -c | sort -rn"))
        self.assertEqual("b,c\n", out(sh, "echo a,b,c | cut -d, -f2-"))
        self.assertEqual("HELLO\n", out(sh, "echo hello | tr a-z A-Z"))
        self.assertEqual("hi\n", out(sh, "echo hi | tee copy.txt && cat copy.txt >/dev/null"))
        self.assertEqual("ERROR disk full\n", out(sh, "ls *.txt | grep log | xargs grep -h disk"))

    def test_jq_subset(self):
        sh = Shell()
        sh.fs.write_text("/home/agent/o.json", '{"orders":[{"id":1,"status":"open","total":5},{"id":2,"status":"shipped","total":7.5}]}')
        self.assertEqual("2\n", out(sh, "jq '.orders | length' o.json"))
        self.assertEqual("open\nshipped\n", out(sh, "jq -r '.orders[].status' o.json"))
        self.assertEqual("2\n", out(sh, "jq '.orders[] | select(.status == \"shipped\") | .id' o.json"))
        self.assertEqual("[1,2]\n", out(sh, "cat o.json | jq -c '[.orders[].id]'"))
        self.assertEqual("12.5\n", out(sh, "jq 'map(.total) | add' <<< \"$(jq -c .orders o.json)\""))
        self.assertEqual('["orders"]\n', out(sh, "jq -c keys o.json"))

    def test_errors_are_bash_like(self):
        sh = Shell()
        r = sh.run("frobnicate --now")
        self.assertEqual(127, r.exit_code)
        self.assertIn("frobnicate: command not found", r.stderr)
        r = sh.run("echo 'unterminated")
        self.assertEqual(2, r.exit_code)
        self.assertIn("unexpected EOF", r.stderr)
        r = sh.run("coproc cat")
        self.assertEqual(2, r.exit_code)
        self.assertIn("not supported", r.stderr)
        r = sh.run("cat nope.txt")
        self.assertEqual(1, r.exit_code)
        self.assertEqual("cat: nope.txt: No such file or directory\n", r.stderr)
        self.assertEqual("cat: nope.txt: No such file or directory\n[exit code 1]", r.to_tool_output())
        self.assertEqual("ls: invalid option -- 'Z'\n", sh.run("ls -Z").stderr)

    def test_fake_system(self):
        sh = Shell(user="dev", hostname="box")
        self.assertEqual("dev\nbox\nLinux\n/home/dev\n", out(sh, "whoami; hostname; uname; echo $HOME"))
        self.assertRegex(out(sh, "date +%F"), r"^\d{4}-\d{2}-\d{2}\n$")
        self.assertEqual("/usr/bin/grep\n", out(sh, "which grep"))
        self.assertIn("1  whoami; hostname", out(sh, "history"))
        out(sh, "alias ll='ls -la'")
        self.assertIn("..", out(sh, "ll"))


class Units(enum.Enum):
    METRIC = "metric"
    IMPERIAL = "imperial"


class WeatherTools:
    @command("weather", description="Get the forecast for a city")
    def forecast(self, city: Annotated[str, "City name"], days: int = 1, units: Units = Units.METRIC, verbose: bool = False) -> str:
        return f"{city}: {days} day(s), {units.name.title()}{' (verbose)' if verbose else ''}"

    @command("orders")
    async def orders(self, ids: list[str]):
        await asyncio.sleep(0)
        return [{"id": i, "status": "open"} for i in ids]

    @command("count-lines")
    def count_lines(self, ctx: CommandContext) -> int:
        ctx.print(len([l for l in ctx.stdin.split("\n") if l]))
        return 0


class CustomCommandTests(unittest.TestCase):
    def test_decorated_commands(self):
        sh = Shell().register(WeatherTools())
        self.assertEqual("Paris: 1 day(s), Metric\n", out(sh, "weather Paris"))
        self.assertEqual("Oslo: 3 day(s), Imperial (verbose)\n", out(sh, "weather --city Oslo --days=3 --units imperial --verbose"))
        self.assertIn('"status": "open"', out(sh, "orders 1 2"))
        self.assertEqual("2\n", out(sh, "printf 'a\\nb\\n' | count-lines"))
        self.assertEqual("Paris: 1 day(s), Metric\n", out(sh, "weather Paris > w.txt && cat w.txt"))
        help_text = out(sh, "weather --help")
        self.assertIn("Usage: weather --city <text> [--days <int>]", help_text)
        self.assertIn("City name (required)", help_text)
        self.assertIn("(metric|imperial)", help_text)
        r = sh.run("weather")
        self.assertEqual(2, r.exit_code)
        self.assertIn("missing required option --city", r.stderr)
        self.assertIn("invalid value 'soon' for --days: expected int", sh.run("weather Rome --days soon").stderr)
        self.assertIn("weather", out(sh, "help"))

    def test_plain_function_and_handler(self):
        @command
        def greet(name: str = "world") -> str:
            """Say hello"""
            return f"hello {name}"

        async def deploy(ctx):
            ctx.print("deploying", ",".join(ctx.args))
            return 0 if ctx.args else 1

        sh = Shell().register(greet).register_command("deploy", deploy, "Deploy a service")
        self.assertEqual("hello bob\n", out(sh, "greet bob"))
        self.assertEqual("deploying api\n", out(sh, "deploy api"))
        self.assertEqual(1, sh.run("deploy").exit_code)
        self.assertEqual("/usr/bin/deploy\n", out(sh, "which deploy"))

    def test_tool_output(self):
        sh = Shell()
        self.assertEqual("(no output)", asyncio.run(execute_tool(sh, "true")))
        self.assertTrue(sh.run("seq_missing").to_tool_output().endswith("[exit code 127]"))
        sh.fs.write_text("/big.txt", "x" * 40_000)
        self.assertIn("output truncated", asyncio.run(execute_tool(sh, "cat /big.txt")))


if __name__ == "__main__":
    unittest.main()
