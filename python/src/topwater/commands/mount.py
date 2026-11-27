"""mount: the sources the host mounted (docs/mounts.md), the changes waiting in them, listing them again, dropping changes."""
from __future__ import annotations

from .._spec import COMMANDS
from ..mounts import _SourceFailure
from ..shell.types import _ControlFlow
from ..vfs import normalize
from . import Args, spec_message


def _line(m) -> str:
    """`TYPE:ID on DIR type TYPE (OPTIONS)`, the options naming what a writable mount's source can't do."""
    opts = ["ro"] if m.mode == "read-only" else ["rw", m.mode] + [f"no-{c}" for c in ("delete", "rename", "mkdir") if c not in m.capabilities()]
    return f"{m.type}:{m.id} on {m.path} type {m.type} ({','.join(opts)})" + (f" {m.shown}" if m.shown else "")


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
            except _SourceFailure as e:
                ctx.error(f"mount: {m.path}: {e.safe}")
                code = 1
            for w in m.warnings:
                ctx.error("mount: " + w)
        elif a.has("d"):
            mounts.discard(m.path, held=False)
        elif a.has("c"):
            for c in m.changes():
                ctx.print(_change(m, c))
        else:
            ctx.print(_line(m))
            for w in m.warnings + ([spec_message("mount", "held", count=str(len(m.held)))] if m.held else []):
                ctx.print("  " + w)
    return code


def register(shell) -> None:
    shell.register_command("mount", _mount)
