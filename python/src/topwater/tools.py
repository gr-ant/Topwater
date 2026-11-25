"""Schema-described tools (the shape MCP and function calling use) exposed as one program with a subcommand per tool.

    register_tools(shell, "gh", [ToolSpec("list_issues", "List issues", {"type": "object", "properties": {...}})], invoke)
    # the agent: gh --help | gh list-issues --help | gh list-issues --repo a/b --limit 5

Options come from each tool's JSON Schema: strings, integers, numbers, booleans (`--flag`, `--no-flag`), repeatable
arrays, JSON objects, enums, `$ref` and `Optional[T]` (anyOf with null) as Pydantic writes them, plus
`--json OBJECT` ('-' reads stdin) to pass everything at once.
"""
from __future__ import annotations

import inspect
import json
import math
import re
from dataclasses import dataclass, field
from typing import Any, Awaitable, Callable, Iterable


@dataclass(frozen=True)
class ToolSpec:
    """A tool described by a name and a JSON Schema for its arguments."""
    name: str
    description: str | None = None
    input_schema: Any = field(default_factory=dict)


@dataclass(frozen=True)
class ToolResult:
    """What a tool call returned. An error goes to stderr and makes the command exit 1."""
    text: str
    is_error: bool = False


# invoke(tool_name, arguments) -> ToolResult or str (sync or async); a returned str is a successful result
ToolInvoker = Callable[[str, dict], "ToolResult | str | Awaitable[ToolResult | str]"]


class _Usage(Exception):
    pass


class _HelpRequested(Exception):
    pass


def _kebab(s: str) -> str:
    return s.replace("_", "-").replace(".", "-").replace("/", "-").replace(" ", "-")


def _first_line(s: str | None) -> str:
    return (s or "").strip().split("\n")[0].strip()


def _prop(e: Any, name: str):
    return e.get(name) if isinstance(e, dict) else None


def _reject_constant(name: str):
    raise ValueError(f"{name} is not JSON")


def _loads(raw: str):
    return json.loads(raw, parse_constant=_reject_constant)


def _try_json(raw: str):
    try:
        return _loads(raw)
    except ValueError:
        return None


@dataclass
class _Param:
    name: str
    flag: str
    type: str
    item_type: str | None
    required: bool
    schema: Any  # the property as declared (description, default)
    shape: Any  # the same after following $ref and unwrapping Optional[T] (type, items, enum)


def _resolve(schema: Any, root: Any, depth: int = 0) -> Any:
    """Follows a local $ref and unwraps "T or null" (how Pydantic writes Optional[T]) to T's schema."""
    if not isinstance(schema, dict) or depth > 8:
        return schema
    ref = schema.get("$ref")
    if isinstance(ref, str) and ref.startswith("#/"):
        target = root
        for seg in ref[2:].split("/"):
            target = _prop(target, seg.replace("~1", "/").replace("~0", "~"))
            if target is None:
                return schema
        return _resolve(target, root, depth + 1)
    for key in ("anyOf", "oneOf"):
        options = schema.get(key)
        if isinstance(options, list):
            real = [o for o in (_resolve(o, root, depth + 1) for o in options) if _type_of(o) != "null"]
            if len(real) == 1:
                return real[0]
    return schema


def _type_of(schema: Any) -> str:
    """The JSON Schema type of a resolved property; "" when it is not stated or is a union of several types."""
    if not isinstance(schema, dict):
        return ""
    t = schema.get("type")
    if isinstance(t, str):
        return t
    if isinstance(t, list):
        # ["string","null"] is a string; ["integer","string"] accepts either, so the value is not type-checked here
        real = sorted({x for x in t if isinstance(x, str) and x != "null"})
        return real[0] if len(real) == 1 else ""
    e = schema.get("enum")
    return "string" if isinstance(e, list) and all(isinstance(x, str) for x in e) else ""


def _params(tool: ToolSpec) -> list[_Param]:
    schema = tool.input_schema
    props = _prop(schema, "properties")
    if not isinstance(props, dict):
        return []
    req = _prop(schema, "required")
    required = {r for r in req if isinstance(r, str)} if isinstance(req, list) else set()
    out = []
    for name, value in props.items():
        shape = _resolve(value, schema)
        typ = _type_of(shape)
        item = _type_of(_resolve(_prop(shape, "items"), schema)) if typ == "array" and _prop(shape, "items") is not None else None
        out.append(_Param(name, "--" + _kebab(name), typ, item, name in required, value, shape))
    return out


def _placeholder(p: _Param) -> str:
    return {"integer": "INT", "number": "NUM", "object": "JSON", "string": "TEXT", "boolean": "BOOL"}.get(
        p.item_type if p.type == "array" else p.type, "VALUE")


def _parse_bool(s: str, flag: str) -> bool:
    v = s.lower()
    if v in ("true", "1", "yes"):
        return True
    if v in ("false", "0", "no"):
        return False
    raise _Usage(f"option {flag} expects true or false, got '{s}'")


_INT = re.compile(r"\s*[+-]?\d+\s*\Z")
_NUM = re.compile(r"\s*[+-]?(\d+\.?\d*|\.\d+)([eE][+-]?\d+)?\s*\Z")


def _convert(raw: str, typ: str, flag: str):
    if typ == "string":
        return raw
    if typ == "integer":
        if not _INT.match(raw):
            raise _Usage(f"option {flag} expects a whole number, got '{raw}'")
        return int(raw)
    if typ == "number":
        if not _NUM.match(raw) or not math.isfinite(float(raw)):
            raise _Usage(f"option {flag} expects a number, got '{raw}'")
        return float(raw)
    if typ == "boolean":
        return _parse_bool(raw, flag)
    if typ == "object":
        v = _try_json(raw)
        if not isinstance(v, dict):
            raise _Usage(f"option {flag} expects a JSON object, got '{raw}'")
        return v
    # no declared type: JSON if it looks like JSON (numbers, true, objects, arrays), otherwise text
    if raw and (raw[0].isdigit() or raw[0] in '{["-' or raw in ("true", "false", "null")):
        try:
            return _loads(raw)
        except ValueError:
            pass
    return raw


class _ToolGroup:
    def __init__(self, program: str, description: str | None, tools: list[ToolSpec], invoke: ToolInvoker):
        self.program, self.description, self.tools, self.invoke = program, description, tools, invoke

    async def run(self, ctx) -> int:
        args = list(ctx.args)
        program = self.program
        if not args or args[0] in ("--help", "-h", "help"):
            ctx.stdout.write(self.group_help())
            return 0
        tool = next((t for t in self.tools if t.name == args[0]), None) or next((t for t in self.tools if _kebab(t.name) == _kebab(args[0])), None)
        if tool is None:
            ctx.stderr.write(f"{program}: unknown tool '{args[0]}'. Run `{program} --help` to list tools.\n")
            return 2
        params = _params(tool)
        try:
            call = self.parse(params, args[1:], ctx)
        except _HelpRequested:
            ctx.stdout.write(self.tool_help(tool, params))
            return 0
        except _Usage as e:
            ctx.stderr.write(f"{program} {_kebab(tool.name)}: {e}\nTry `{program} {_kebab(tool.name)} --help`.\n")
            return 2

        result = self.invoke(tool.name, call)
        if inspect.isawaitable(result):
            result = await result
        if isinstance(result, str):
            result = ToolResult(result)
        text = result.text if not result.text or result.text.endswith("\n") else result.text + "\n"
        if not result.is_error:
            ctx.stdout.write(text)
            return 0
        ctx.stderr.write(text or f"{program} {_kebab(tool.name)}: the tool reported an error\n")
        return 1

    def group_help(self) -> str:
        out = []
        if self.description and self.description.strip():
            out.append(self.description.strip() + "\n\n")
        out.append(f"Usage: {self.program} TOOL [--option value]...\n       {self.program} TOOL --help\n\nTools:\n")
        width = min(32, max((len(_kebab(t.name)) for t in self.tools), default=0))
        for t in self.tools:
            out.append(f"  {_kebab(t.name).ljust(width)}  {_first_line(t.description)}\n")
        return "".join(out)

    def tool_help(self, tool: ToolSpec, params: list[_Param]) -> str:
        left = [p.flag if p.type == "boolean" else f"{p.flag} {_placeholder(p)}" for p in params]
        usage = " ".join(l for p, l in zip(params, left) if p.required)
        out = [f"Usage: {self.program} {_kebab(tool.name)}{' ' + usage if usage else ''}{' [options]' if any(not p.required for p in params) else ''}\n"]
        if tool.description and tool.description.strip():
            out.append("\n" + tool.description.strip() + "\n")
        if params:
            out.append("\nOptions:\n")
            width = min(34, max(len(l) for l in left))
            for p, l in zip(params, left):
                notes = []
                d = _prop(p.schema, "description")
                if not isinstance(d, str):
                    d = _prop(p.shape, "description")
                if isinstance(d, str):
                    notes.append(" ".join(d.strip().splitlines()))
                if p.required:
                    notes.append("(required)")
                if p.type == "array":
                    notes.append("(repeatable)")
                e = _prop(p.shape, "enum")
                if isinstance(e, list):
                    notes.append("(one of: " + ", ".join(x if isinstance(x, str) else json.dumps(x) for x in e) + ")")
                if isinstance(p.schema, dict) and "default" in p.schema:
                    dv = p.schema["default"]
                    notes.append(f"(default: {dv if isinstance(dv, str) else json.dumps(dv)})")
                out.append(f"  {l.ljust(width)}  {' '.join(notes)}\n")
        out.append("\n" + "  --json JSON".ljust(36) + "pass all arguments as one JSON object ('-' reads it from stdin)\n")
        return "".join(out)

    def parse(self, params: list[_Param], args: list[str], ctx) -> dict:
        # --json gives the base object and named options override it, whichever comes first on the command line
        call: dict = {}
        from_json: dict = {}
        i = 0
        while i < len(args):
            arg = args[i]
            # only in option position, so `--title -h` passes "-h" as the title
            if arg in ("--help", "-h") and not any(x.flag == "--help" for x in params):
                raise _HelpRequested()
            if not arg.startswith("--") or len(arg) == 2:
                raise _Usage(f"unexpected argument '{arg}'; arguments are passed as --name value")
            flag, inline, has_inline = arg, "", False
            eq = arg.find("=")
            if eq > 0:
                flag, inline, has_inline = arg[:eq], arg[eq + 1:], True

            def nxt() -> str:
                nonlocal i
                if has_inline:
                    return inline
                i += 1
                if i >= len(args):
                    raise _Usage(f"option {flag} needs a value")
                return args[i]

            if flag == "--json" and not any(x.flag == "--json" for x in params):
                raw = nxt()
                if raw == "-":
                    raw = ctx.stdin
                try:
                    obj = _loads(raw)
                except ValueError as e:
                    raise _Usage(f"--json is not valid JSON: {e}") from None
                if not isinstance(obj, dict):
                    raise _Usage("--json must be a JSON object")
                from_json.update(obj)
                i += 1
                continue

            p = next((x for x in params if x.flag == flag or "--" + x.name == flag), None)
            negated = False
            if p is None and flag.startswith("--no-"):
                neg = next((x for x in params if x.type == "boolean" and x.flag == "--" + flag[5:]), None)
                if neg is not None:
                    p, negated = neg, True
            if p is None:
                known = "this tool takes no options" if not params else "options are " + ", ".join(x.flag for x in params)
                raise _Usage(f"unknown option {flag}; {known}")

            if p.type == "boolean":
                value = not negated
                # a following true/false is taken as the value, so `--flag false` works as well as `--no-flag`
                if has_inline:
                    value = _parse_bool(inline, flag) != negated
                elif not negated and i + 1 < len(args) and args[i + 1] in ("true", "false"):
                    i += 1
                    value = args[i] == "true"
                call[p.name] = value
            elif p.type == "array":
                raw = nxt()
                array = call.setdefault(p.name, [])
                whole = _try_json(raw) if raw.lstrip().startswith("[") else None
                if isinstance(whole, list):
                    array.extend(whole)
                else:
                    array.append(_convert(raw, p.item_type or "", flag))
            else:
                call[p.name] = _convert(nxt(), p.type, flag)
            i += 1

        from_json.update(call)
        missing = [p.flag for p in params if p.required and p.name not in from_json]
        if missing:
            raise _Usage("missing required option" + ("s " if len(missing) > 1 else " ") + ", ".join(missing))
        return from_json


def register_tools(shell, program: str, tools: Iterable[ToolSpec], invoke: ToolInvoker, description: str | None = None):
    """Exposes a set of schema-described tools as one program with a subcommand per tool: `program` lists them,
    `program TOOL --help` documents one, and `program TOOL --some-arg value` calls it."""
    listed = sorted(tools, key=lambda t: t.name)
    group = _ToolGroup(program, description, listed, invoke)
    shell.register_command(program, group.run, description or f"{len(listed)} tools; run `{program} --help`",
                           f"{program} TOOL [--option value]...")
    return shell
