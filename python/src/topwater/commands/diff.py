"""diff: the line diff engine and its output formats (the command is registered with the text commands). See docs/architecture.md."""
from __future__ import annotations

import difflib
import re

from .._spec import COMMANDS
from ..vfs import ShellError, UsageException, get_name
from . import Args, spec_message, split_lines, write_lines


def _diff(ctx):
    a = Args.parse(ctx.args, COMMANDS["diff"])
    if len(a.positional) != 2:
        raise UsageException(spec_message("diff", "missingOperandAfter", operand=a.positional[-1] if a.positional else "diff")
                             if len(a.positional) < 2 else spec_message("diff", "extraOperand", operand=a.positional[2]))
    an, bn = a.positional
    for p in (an, bn):
        if p != "-" and not a.has("N") and not ctx.fs.exists(ctx.resolve(p)):
            raise ShellError(spec_message("diff", "fileNotFound", file=p), 2)

    def is_dir(p):
        return p != "-" and ctx.fs.is_dir(ctx.resolve(p))

    if is_dir(an) and is_dir(bn):
        return _diff_dirs(ctx, a, an, bn, 0)
    # a directory and a file: compare the file with the one of the same name in the directory
    if is_dir(an):
        an = _join(an, get_name(bn))
    elif is_dir(bn):
        bn = _join(bn, get_name(an))
    return _diff_files(ctx, a, an, bn, None)


def _join(d, name):
    return d + name if d.endswith("/") else d + "/" + name


def _diff_dirs(ctx, a, an, bn, depth):
    """Two directories: entries only one side has, then each common one in name order (subdirectories with -r)."""
    if depth > 64:
        raise ShellError(spec_message("diff", "tooLarge"), 2)  # shortcut: a symlink loop ends here, not with GNU's loop message
    fs = ctx.fs
    pa, pb = ctx.resolve(an), ctx.resolve(bn)
    left, right = set(fs.listdir(pa)), set(fs.listdir(pb))
    code = 0
    for name in sorted(left | right):
        x, y = _join(an, name), _join(bn, name)
        in_a, in_b = name in left, name in right
        dir_a, dir_b = in_a and fs.is_dir(_join(pa, name)), in_b and fs.is_dir(_join(pb, name))
        if not in_a or not in_b:
            if a.has("N") and not (dir_a or dir_b):
                rc = _diff_files(ctx, a, x, y, _diff_header(ctx, a, x, y))
            else:
                ctx.print(spec_message("diff", "onlyIn", dir=an if in_a else bn, name=name))
                rc = 1
        elif dir_a and dir_b:
            if a.has("r"):
                rc = _diff_dirs(ctx, a, x, y, depth + 1)
            else:
                ctx.print(spec_message("diff", "commonSubdirectories", file1=x, file2=y))
                rc = 0
        elif dir_a or dir_b:
            ctx.print(spec_message("diff", "typeMismatch", file1=x, kind1="directory" if dir_a else "regular file", file2=y, kind2="directory" if dir_b else "regular file"))
            rc = 1
        else:
            rc = _diff_files(ctx, a, x, y, _diff_header(ctx, a, x, y))
        code = max(code, rc)
    return code


def _diff_header(ctx, a, x, y):
    """The line GNU diff prints before each differing pair in a directory: the options as typed, then both names."""
    options = list(ctx.args)
    for operand in a.positional:
        if operand in options:
            options.remove(operand)
    return " ".join(["diff", *options, x, y])


def _diff_files(ctx, a, an, bn, header):
    def read(p):
        if p == "-":
            return ctx.stdin
        path = ctx.resolve(p)
        if not ctx.fs.exists(path):
            if a.has("N"):
                return ""
            raise ShellError(spec_message("diff", "fileNotFound", file=p), 2)
        return ctx.fs.read_text(path)

    x, y = split_lines(read(an)), split_lines(read(bn))

    def key(line):
        if a.has("w"):
            line = "".join(line.split())
        elif a.has("b"):
            line = re.sub(r"\s+", " ", line.rstrip())
        return line.lower() if a.has("i") else line

    sm = difflib.SequenceMatcher(None, [key(l) for l in x], [key(l) for l in y], autojunk=False)
    opcodes = sm.get_opcodes()
    if all(tag == "equal" for tag, *_ in opcodes):
        if a.has("s"):
            ctx.print(f"Files {an} and {bn} are identical")
        return 0
    if a.has("q"):
        ctx.print(f"Files {an} and {bn} differ")
        return 1
    if header is not None:
        ctx.print(header)

    def rng(start, count):
        return str(start) if count == 1 else f"{start},{start + count - 1}"

    if a.has("u") or a.value("U") is not None:
        n = int(a.value("U") or 3)
        ctx.print(f"--- {an}")
        ctx.print(f"+++ {bn}")
        for group in sm.get_grouped_opcodes(n):
            i1, i2, j1, j2 = group[0][1], group[-1][2], group[0][3], group[-1][4]
            def hunk_range(start, count):
                return str(start) if count == 1 else f"{start},{count}"  # GNU leaves out a count of 1

            ctx.print(f"@@ -{hunk_range(i1 + (1 if i2 > i1 else 0), i2 - i1)} +{hunk_range(j1 + (1 if j2 > j1 else 0), j2 - j1)} @@")
            for tag, a1, a2, b1, b2 in group:
                if tag == "equal":
                    write_lines(ctx.stdout, (" " + l for l in x[a1:a2]))
                    continue
                write_lines(ctx.stdout, ("-" + l for l in x[a1:a2]))
                write_lines(ctx.stdout, ("+" + l for l in y[b1:b2]))
        return 1
    for tag, a1, a2, b1, b2 in opcodes:
        if tag == "equal":
            continue
        if tag == "replace":
            ctx.print(f"{rng(a1 + 1, a2 - a1)}c{rng(b1 + 1, b2 - b1)}")
        elif tag == "delete":
            ctx.print(f"{rng(a1 + 1, a2 - a1)}d{b1}")
        else:
            ctx.print(f"{a1}a{rng(b1 + 1, b2 - b1)}")
        write_lines(ctx.stdout, ("< " + l for l in x[a1:a2]))
        if tag == "replace":
            ctx.print("---")
        write_lines(ctx.stdout, ("> " + l for l in y[b1:b2]))
    return 1
