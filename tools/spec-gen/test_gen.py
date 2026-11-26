"""Checks the generator and the ports' use of spec/. python3 tools/spec-gen/test_gen.py"""
import copy
import re
import unittest

import gen

SCHEMA = gen.read_json(gen.SPEC / "command.schema.json")
HEAD = gen.read_json(gen.SPEC / "commands" / "head.json")
SHELL, COMMANDS = gen.load()
BY_NAME = {c["name"]: c for c in COMMANDS}
DEFAULTS = SHELL["defaults"]["messages"]

# how each port spells a subcommand message lookup: (command, subcommand, key)
SUB_LOOKUPS = {
    "cs": r'CommandSpec\.SubMessage\(("[^"]+"), ("[^"]+"|[\w.]+), "(\w+)"',
    "py": r'spec_sub_message\(("[^"]+"), ("[^"]+"|[\w.]+), "(\w+)"',
    "ts": r"specSubMessage\(('[^']+'), ('[^']+'|[\w.]+), '(\w+)'",
}

# each port's hand-written sources (the generated tables left out) and how it spells a lookup
PORTS = {
    "cs": ([p for p in (gen.ROOT / "src/Topwater").rglob("*.cs") if "Generated" not in p.parts],
           r'CommandSpec\.Message\(("[^"]+"|[\w.]+), "(\w+)"',
           [r'CommandSpec\.Diagnostics\.(\w+)'], [r'CommandSpec\.Errno\.(\w+)']),
    "py": ([p for p in (gen.ROOT / "python/src/topwater").rglob("*.py") if p.name != "_spec.py"],
           r'spec_message\(("[^"]+"|[\w.]+), "(\w+)"',
           [r'DIAGNOSTICS\["(\w+)"\]', r'diagnostic\("(\w+)"'], [r"ERRNO\[[\"'](\w+)[\"']\]"]),
    "ts": ([p for p in (gen.ROOT / "typescript/src").rglob("*.ts") if p.name != "spec.generated.ts"],
           r"specMessage\(('[^']+'|[\w.]+), '(\w+)'",
           [r'DIAGNOSTICS\.(\w+)', r"diagnostic\('(\w+)'"], [r'ERRNO\.(\w+)']),
}


def lower_first(s):
    return s[0].lower() + s[1:]


def check(c):
    gen.validate(c, SCHEMA, SCHEMA, "head")


class Validate(unittest.TestCase):
    def test_rejects(self):
        bad = {
            "help as a string": lambda c: c.update(help="Usage: head"),
            "two-letter short": lambda c: c["options"][0].update(short="cc"),
            "long with =": lambda c: c["options"][0].update(long=["by=tes"]),
            "string exit code": lambda c: c.update(usageExitCode="2"),
            "non-string message": lambda c: c["messages"].update(openFailed=3),
            "unknown key": lambda c: c.update(foo=1),
            "unknown category": lambda c: c.update(category="misc"),
            "long-only without key": lambda c: c["options"].append({"long": ["x"]}),
            "subcommand without usage": lambda c: c.update(subcommands={"x": {"summary": "s"}}),
            "unknown subcommand key": lambda c: c.update(subcommands={"x": {"summary": "s", "usage": ["git x"], "foo": 1}}),
        }
        for name, change in bad.items():
            c = copy.deepcopy(HEAD)
            change(c)
            with self.subTest(name), self.assertRaises(gen.SpecError):
                check(c)


class Subcommands(unittest.TestCase):
    SUB = {"summary": "Do x", "usage": ["git x [<options>]"], "options": [
        {"short": "q", "long": ["quiet"], "description": "say less"},
        {"short": "m", "long": ["message"], "arg": "<msg>", "description": "the message"}]}

    def command(self, **sub):
        return {"name": "git", "category": "utility", "summary": "s", "parser": "custom", "subcommands": {"x": {**self.SUB, **sub}}}

    def test_table_and_default_help(self):
        s = gen.resolve_subcommands(self.command(), "py")["x"]
        self.assertEqual(s["options"], ("q", "m", "quiet=q,message=m"))
        self.assertEqual(s["help"], "usage: git x [<options>]\n\nDo x.\n\n    -q, --quiet          say less\n    -m, --message <msg>  the message\n")

    def test_ports(self):
        self.assertEqual(gen.resolve_subcommands(self.command(ports=["cs"]), "py"), {})

    def test_needs_custom_parser(self):
        c = self.command()
        c["parser"] = "args"
        with self.assertRaises(gen.SpecError):
            gen.check_command(c, "git")


class PortsUseTheSpec(unittest.TestCase):
    """A missing message key fails only on the error path at run time, so check every lookup here."""

    def test_message_keys_exist(self):
        every_key = set(DEFAULTS) | {k for c in COMMANDS for k in c.get("messages", {})}
        for port, (files, message, _, _) in PORTS.items():
            for f in files:
                for cmd, key in re.findall(message, f.read_text(encoding="utf-8")):
                    with self.subTest(port=port, file=f.name, key=key):
                        if cmd[0] in "\"'":
                            c = BY_NAME[cmd[1:-1]]
                            self.assertIn(key, set(c.get("messages", {})) | set(DEFAULTS))
                        else:  # looked up by the command's own name at run time
                            self.assertIn(key, every_key)

    def test_subcommand_message_keys_exist(self):
        for port, rx in SUB_LOOKUPS.items():
            for f in PORTS[port][0]:
                for cmd, sub, key in re.findall(rx, f.read_text(encoding="utf-8")):
                    c = BY_NAME[cmd[1:-1]]
                    subs = c.get("subcommands", {})
                    known = set(c.get("messages", {})) | set(DEFAULTS)
                    known |= set(subs[sub[1:-1]].get("messages", {})) if sub[0] in "\"'" else {k for s in subs.values() for k in s.get("messages", {})}
                    with self.subTest(port=port, file=f.name, key=key):
                        self.assertIn(key, known)

    def test_every_message_key_is_used(self):
        # used: looked up under its command, or (for keys the code picks at run time, such as cp/mv's clash keys and
        # the unreadable-file helper's openFailed/readDirectory) written as a string somewhere in a port
        sources = {p: "\n".join(f.read_text(encoding="utf-8") for f in files) for p, (files, _, _, _) in PORTS.items()}
        lookups = {(cmd.strip("\"'"), key) for p, (_, message, _, _) in PORTS.items() for cmd, key in re.findall(message, sources[p])}
        for c in COMMANDS:
            for sub, s in c.get("subcommands", {}).items():
                for key in s.get("messages", {}):
                    with self.subTest(command=c["name"], subcommand=sub, key=key):
                        self.assertTrue(any(re.search(rf"[\"']{key}[\"']", text) for text in sources.values()), "no port uses it")
            for key in c.get("messages", {}):
                with self.subTest(command=c["name"], key=key):
                    quoted = any(re.search(rf"[\"']{key}[\"']", text) for text in sources.values())
                    self.assertTrue((c["name"], key) in lookups or quoted, "no port uses it")
        for key in DEFAULTS:
            with self.subTest(default=key):
                self.assertTrue(any(re.search(rf"[\"']{key}[\"']", text) for text in sources.values()), "no port uses it")

    def test_diagnostics_and_errno_are_used_by_every_port(self):
        for port, (files, _, diag, errno) in PORTS.items():
            text = "\n".join(f.read_text(encoding="utf-8") for f in files)
            used_diag = {lower_first(k) if port == "cs" else k for rx in diag for k in re.findall(rx, text)}
            used_errno = {k for rx in errno for k in re.findall(rx, text)}
            with self.subTest(port=port):
                self.assertEqual(sorted(set(SHELL["diagnostics"]) - used_diag), [])   # in the spec, not used
                self.assertEqual(sorted(used_diag - set(SHELL["diagnostics"])), [])   # used, not in the spec
                self.assertEqual(sorted(used_errno - set(SHELL["errno"])), [])


if __name__ == "__main__":
    unittest.main()
