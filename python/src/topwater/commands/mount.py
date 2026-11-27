"""mount: the sources the host mounted (docs/mounts.md), the changes waiting in them, and listing them again."""
from __future__ import annotations

from .._spec import COMMANDS
from ..shell.types import _ControlFlow
from ..vfs import normalize
from . import Args, spec_message


def _line(info) -> str:
    opts = "ro" if info.mode == "read-only" else "rw," + info.mode
    return f"{info.type}:{info.id} on {info.path} type {info.type} ({opts})" + (f" {info.error}" if info.error else "")


def _change(m, c) -> str:
    return f"rename {m.machine(c.path)} -> {m.machine(c.to)}" if c.kind == "rename" else f"{c.kind} {m.machine(c.path)}"


async def _mount(ctx) -> int:
    a = Args.parse(ctx.args, COMMANDS["mount"])
    mounts = ctx.shell._mounts
    code = 0
    chosen = []
    for d in a.positional:
        m = mounts.items.get(normalize(ctx.shell._cwd, d))
        if m is None:
            ctx.error("mount: " + spec_message("mount", "notMounted", dir=d))
            code = 1
        else:
            chosen.append(m)
    if not a.positional:
        chosen = sorted(mounts.items.values(), key=lambda m: m.path)
    for m in chosen:
        if a.has("r"):
            try:
                await mounts.refresh(m.path)
            except _ControlFlow:
                raise
            except Exception as e:  # the source's own error, for the agent
                ctx.error(f"mount: {m.path}: {e}")
                code = 1
            for w in m.warnings:
                ctx.error("mount: " + w)
        elif a.has("c"):
            for c in m.changes():
                ctx.print(_change(m, c))
        else:
            ctx.print(_line(m.info()))
    return code


def register(shell) -> None:
    shell.register_command("mount", _mount)
