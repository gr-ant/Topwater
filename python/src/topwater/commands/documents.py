"""to-markdown: converts documents in the virtual filesystem to Markdown."""
from __future__ import annotations

from ..convert import to_markdown
from ..vfs import ShellError
from . import Args


def _to_markdown(ctx):
    a = Args.parse(ctx.args, "", "o", "output=o")
    if not a.positional:
        raise ShellError("missing file operand\nUsage: to-markdown [-o OUT.md] FILE...")
    code, parts = 0, []
    for f in a.positional:
        try:
            md = to_markdown(ctx.fs.read_bytes(ctx.resolve(f)), f)
        except ShellError as e:
            # filesystem errors carry the path ("/abs/x: No such file..."); show the name as typed instead
            ctx.error(f"{ctx.name}: {f}: {str(e).rsplit(': ', 1)[-1] if str(e).startswith('/') else e}")
            code = 1
            continue
        md = md if md.endswith("\n") or not md else md + "\n"
        parts.append(f"# {f}\n\n{md}" if len(a.positional) > 1 else md)
    text = "\n".join(parts)
    if a.value("o") is not None:
        ctx.fs.write_text(ctx.resolve(a.value("o")), text)
    else:
        ctx.stdout.write(text)
    return code


def register(shell) -> None:
    shell.register_command("to-markdown", _to_markdown,
                           "Convert documents (docx, xlsx, pptx, pdf, html, csv, json) to Markdown on stdout (-o FILE to save)")
