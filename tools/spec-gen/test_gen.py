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
        }
        for name, change in bad.items():
            c = copy.deepcopy(HEAD)
            change(c)
            with self.subTest(name), self.assertRaises(gen.SpecError):
                check(c)


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
