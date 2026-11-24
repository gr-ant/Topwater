"""@command: expose a Python function or method as a shell program."""
from __future__ import annotations

import dataclasses
import enum
import inspect
import json
import re
import types
import typing
from typing import Annotated, Any, Callable

from .vfs import ShellError

_MARK = "__topwater_command__"


def command(name: str | None = None, description: str = "") -> Callable:
    """Marks a function/method as a shell command.

    Parameters become --kebab-case options (and positionals, in order); bool parameters become flags;
    ``Annotated[str, "help text"]`` documents an option. Return a str (stdout), int (exit code), None,
    or any other value (printed as JSON). Async functions are supported. A parameter annotated
    ``CommandContext`` receives the context (stdin, stdout, filesystem, ...).
    """

    def mark(fn):
        setattr(fn, _MARK, (name or fn.__name__.replace("_", "-"), description or inspect.getdoc(fn) or ""))
        return fn

    if callable(name):  # bare @command
        fn, name = name, None
        return mark(fn)
    return mark


def register_targets(shell, targets) -> None:
    for target in targets:
        if callable(target) and hasattr(target, _MARK) and not inspect.isclass(target):
            _register_fn(shell, target)
            continue
        for attr in dir(target):
            if attr.startswith("__"):
                continue
            fn = getattr(target, attr, None)
            if callable(fn) and hasattr(fn, _MARK):
                _register_fn(shell, fn)


def _register_fn(shell, fn) -> None:
    name, description = getattr(fn, _MARK)
    bound = _BoundCommand(fn, name, description)
    shell.register_command(name, bound.invoke, description.split("\n")[0], bound.usage)


def _kebab(name: str) -> str:
    return re.sub(r"([a-z0-9])([A-Z])", r"\1-\2", name).replace("_", "-").lower()


@dataclasses.dataclass
class _Param:
    name: str
    option: str
    type: Any
    is_list: bool
    is_flag: bool
    injected: bool
    required: bool
    default: Any
    help: str


def _unwrap(tp):
    """Returns (base type, help text, is_list) for an annotation."""
    help_text = ""
    if typing.get_origin(tp) is Annotated:
        tp, *meta = typing.get_args(tp)
        help_text = next((m for m in meta if isinstance(m, str)), "")
    origin = typing.get_origin(tp)
    if origin in (typing.Union, types.UnionType):
        args = [a for a in typing.get_args(tp) if a is not type(None)]
        tp = args[0] if len(args) == 1 else str
        origin = typing.get_origin(tp)
    if origin in (list, tuple, set) or tp in (list, tuple, set):
        args = typing.get_args(tp)
        return (args[0] if args else str), help_text, True
    return (tp if tp is not inspect.Parameter.empty else str), help_text, False


def _type_name(t) -> str:
    return {str: "text", int: "int", float: "number", bool: "bool"}.get(t, "choice" if isinstance(t, type) and issubclass(t, enum.Enum) else getattr(t, "__name__", "value").lower())


class _BoundCommand:
    def __init__(self, fn, name: str, description: str):
        from .shell import CommandContext

        self.fn = fn
        self.name = name
        hints = typing.get_type_hints(fn, include_extras=True)
        self.params: list[_Param] = []
        for p in inspect.signature(fn).parameters.values():
            if p.kind in (p.VAR_KEYWORD,):
                continue
            tp, help_text, is_list = _unwrap(hints.get(p.name, str))
            is_list = is_list or p.kind == p.VAR_POSITIONAL
            has_default = p.default is not inspect.Parameter.empty
            self.params.append(_Param(
                name=p.name, option=_kebab(p.name), type=tp, is_list=is_list, is_flag=tp is bool and not is_list,
                injected=tp is CommandContext, required=not has_default and tp is not bool and not is_list,
                default=p.default if has_default else None, help=help_text,
            ))
        self.usage = self._build_usage(description)

    def _build_usage(self, description: str) -> str:
        head = f"Usage: {self.name}"
        options = []
        for p in self.params:
            if p.injected:
                continue
            left = f"--{p.option}" if p.is_flag else f"--{p.option} <{_type_name(p.type)}>"
            head += f" {left}" if p.required else f" [{left}]"
            desc = p.help
            if isinstance(p.type, type) and issubclass(p.type, enum.Enum):
                desc += f" ({'|'.join(m.name.lower() for m in p.type)})"
            if p.default not in (None, False) and not p.is_flag:
                d = p.default.name.lower() if isinstance(p.default, enum.Enum) else p.default
                desc += f" [default: {d}]"
            if p.required:
                desc += " (required)"
            options.append((left, desc.strip()))
        text = head
        if description:
            text += "\n\n" + description
        if options:
            width = max(len(o[0]) for o in options) + 2
            text += "\n\nOptions:" + "".join(f"\n  {left.ljust(width)}{desc}" for left, desc in options)
        return text

    def _error(self, message: str) -> ShellError:
        return ShellError(f"{message}\n{self.usage}", 2)

    def _convert(self, raw: str, p: _Param):
        t = p.type
        try:
            if t is str or t is Any:
                return raw
            if t is bool:
                low = raw.lower()
                if low in ("true", "yes", "1"):
                    return True
                if low in ("false", "no", "0"):
                    return False
                raise ValueError
            if isinstance(t, type) and issubclass(t, enum.Enum):
                for m in t:
                    if raw.lower().replace("-", "_") in (m.name.lower(), str(m.value).lower()):
                        return m
                raise ValueError
            return t(raw)
        except (ValueError, TypeError):
            raise self._error(f"invalid value '{raw}' for --{p.option}: expected {_type_name(t)}") from None

    async def invoke(self, ctx) -> int:
        if "--help" in ctx.args or "-h" in ctx.args:
            ctx.stdout.write(self.usage + "\n")
            return 0

        values: dict[str, Any] = {}
        lists: dict[str, list] = {}
        positional: list[str] = []

        def assign(p: _Param, raw: str):
            if p.is_list:
                lists.setdefault(p.name, []).append(self._convert(raw, p))
            else:
                values[p.name] = self._convert(raw, p)

        args = ctx.args
        a = 0
        while a < len(args):
            arg = args[a]
            if arg == "--":
                positional.extend(args[a + 1:])
                break
            if not arg.startswith("--") or len(arg) == 2:
                positional.append(arg)
                a += 1
                continue
            name, _, inline = arg[2:].partition("=")
            has_inline = "=" in arg
            p = next((p for p in self.params if p.option == name.lower() and not p.injected), None)
            if p is None:
                raise self._error(f"unknown option '--{name}'")
            if p.is_flag:
                assign(p, inline if has_inline else "true")
            elif has_inline:
                assign(p, inline)
            elif a + 1 < len(args):
                a += 1
                assign(p, args[a])
            else:
                raise self._error(f"option '--{name}' requires a value")
            a += 1

        nxt = 0
        for p in self.params:
            if nxt >= len(positional):
                break
            if p.injected or p.is_flag or (p.name in values and not p.is_list):
                continue
            if p.is_list:
                while nxt < len(positional):
                    assign(p, positional[nxt])
                    nxt += 1
            else:
                assign(p, positional[nxt])
                nxt += 1
        if nxt < len(positional):
            raise self._error(f"unexpected argument '{positional[nxt]}'")

        call_args, call_kwargs = [], {}
        sig_params = inspect.signature(self.fn).parameters
        for p in self.params:
            if p.injected:
                v = ctx
            elif p.is_list:
                v = lists.get(p.name, [])
            elif p.name in values:
                v = values[p.name]
            elif p.required:
                raise self._error(f"missing required option --{p.option}")
            else:
                v = p.default if p.default is not None or not p.is_flag else False
            if sig_params[p.name].kind == inspect.Parameter.VAR_POSITIONAL:
                call_args.extend(v)
            elif sig_params[p.name].kind == inspect.Parameter.POSITIONAL_ONLY:
                call_args.append(v)
            else:
                call_kwargs[p.name] = v

        result = self.fn(*call_args, **call_kwargs)
        if inspect.isawaitable(result):
            result = await result

        if result is None:
            return 0
        if isinstance(result, int) and not isinstance(result, bool):
            return result
        if isinstance(result, str):
            ctx.stdout.write(result if not result or result.endswith("\n") else result + "\n")
            return 0
        ctx.stdout.write(json.dumps(result, indent=2, ensure_ascii=False, default=_json_default) + "\n")
        return 0


def _json_default(o):
    if dataclasses.is_dataclass(o) and not isinstance(o, type):
        return dataclasses.asdict(o)
    if hasattr(o, "model_dump"):  # pydantic
        return o.model_dump()
    if isinstance(o, enum.Enum):
        return o.value
    if isinstance(o, (set, tuple)):
        return list(o)
    return str(o)
