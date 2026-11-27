"""Shell: options, scalar variables and their attributes, and arrays. See docs/architecture.md."""
from __future__ import annotations

import random
import re
import time

from .. import arith
from .._spec import DIAGNOSTICS, diagnostic
from ..parser import Assignment, Lexer, Literal, Parser, Word, is_name, split_subscript, try_assignment
from ..vfs import ShellError
from .invocation import trace_quote
from .types import ShellArray, _Io, default_op


_BASH_VERSINFO = ["5", "2", "21", "1", "release", "x86_64-pc-linux-gnu"]
_SCRATCH = "__topwater_element"


class VariablesPart:
    # ---------- options ----------

    def is_variable_set(self, expr: str) -> bool:
        """[[ -v name ]], [[ -v a[k] ]]: whether the variable or element is set."""
        r = array_ref(expr)
        if r is not None and not r[0] and r[3] == "":
            if r[2] in ("@", "*"):
                a = self.get_array(r[1])
                return a is not None and bool(a.items)
            return self.get_element(r[1], r[2]) is not None
        return self._is_set(expr) if is_name(expr) or (expr and expr.isdigit()) else False

    @property
    def option_list(self) -> list[tuple[str, bool]]:
        """The set -o options this shell has, and whether each is on."""
        return [
            ("allexport", False), ("braceexpand", True), ("emacs", False), ("errexit", self.errexit), ("errtrace", False),
            ("functrace", False), ("hashall", True), ("histexpand", False), ("history", False), ("ignoreeof", False),
            ("interactive-comments", True), ("keyword", False), ("monitor", False), ("noclobber", self.noclobber),
            ("noexec", False), ("noglob", self.noglob), ("nolog", False), ("notify", False), ("nounset", self.nounset),
            ("onecmd", False), ("physical", False), ("pipefail", self.pipefail), ("posix", False), ("privileged", False),
            ("verbose", False), ("vi", False), ("xtrace", self.xtrace)]

    def set_option(self, name: str, on: bool) -> bool:
        """set -o NAME / set +o NAME; False for an unknown name."""
        if name in ("errexit", "nounset", "pipefail", "xtrace", "noglob", "noclobber"):
            setattr(self, name, on)
            return True
        return any(n == name for n, _ in self.option_list)  # accepted, no effect here

    def declare_local(self, name: str, value: str | None) -> None:
        """`local NAME[=VALUE]`: remembers the outer value and attributes so they are restored when the function returns."""
        if not self._locals:
            raise ShellError(DIAGNOSTICS["onlyInFunction"])
        frame = self._locals[-1]
        if name not in frame:
            frame[name] = (self.env.get(name), self.var_attrs.get(name))
            self.var_attrs.pop(name, None)
        self.mark_shell_variable(name)
        self.env[name] = value or ""

    @property
    def _option_flags(self) -> str:
        """$-: the single-letter options that are on."""
        return (("e" if self.errexit else "") + ("f" if self.noglob else "") + "h" + ("u" if self.nounset else "")
                + ("x" if self.xtrace else "") + "B" + ("C" if self.noclobber else "") + "c")

    # ---------- scalars ----------

    def has_attr(self, name: str, attr: str) -> bool:
        return bool(self.var_attrs) and attr in self.var_attrs.get(name, "")

    def is_exported(self, name: str) -> bool:
        """Whether a child bash (bash -c, a script) sees the variable: declared -x, or put in env by the host."""
        return self.has_attr(name, "x") or (name in self.env and name not in self._unexported)

    def mark_shell_variable(self, name: str) -> None:
        """Records that the shell (not the host) is about to create name, so it is not exported until `export`."""
        if name not in self.env:
            self._unexported.add(name)

    def unexport(self, name: str) -> None:
        self.set_attrs(name, "", "x")
        self._unexported.add(name)

    def set_attrs(self, name: str, on: str, off: str = "") -> None:
        f = self.var_attrs.get(name, "")
        if "l" in on:
            f = f.replace("u", "")
        if "u" in on:
            f = f.replace("l", "")
        for c in on:
            if c not in f:
                f += c
        for c in off:
            f = f.replace(c, "")
        if f:
            self.var_attrs[name] = f
        else:
            self.var_attrs.pop(name, None)

    def resolve_name(self, name: str) -> str:
        """Follows namerefs (declare -n ref=name) to the variable they stand for."""
        for _ in range(10):
            if not self.has_attr(name, "n") or not self.env.get(name):
                break
            name = self.env[name]
        return name

    def set_var(self, name: str, value: str, append: bool = False) -> None:
        """Assigns a variable the way bash does: through namerefs, refusing readonly ones, and applying -i (arithmetic),
        -l and -u. name[sub] sets an element; a plain assignment to an array sets element 0."""
        name = self.resolve_name(name)
        r = array_ref(name)
        if r is not None and not r[0] and r[3] == "" and r[2] not in ("@", "*"):
            self._check_writable(r[1])
            self.set_element(r[1], r[2], self._converted(self.resolve_name(r[1]), value, self.get_element(r[1], r[2]) if append else None))
            return
        if not is_name(name):
            raise ShellError(diagnostic("notValidIdentifier", name=name))
        self._check_writable(name)
        if name == "PATH":
            self.hashed = {}  # even to the same value
        if name == "SECONDS" and re.fullmatch(r"-?\d+", value):
            self._seconds_base = time.time() - int(value)
            return
        array = name in self.arrays
        value = self._converted(name, value, None if not append else self.get_element(name, "0") if array else self.env.get(name))
        if array:
            self.set_element(name, "0", value)
        else:
            self.charge_variable(name, len(value), len(self.env.get(name, "")))
            self.mark_shell_variable(name)
            self.env[name] = value

    def _converted(self, name: str, value: str, old: str | None) -> str:
        f = self.var_attrs.get(name) if self.var_attrs else None
        if not f:
            return (old or "") + value
        if "i" in f:
            return str(arith.evaluate(self, value) + (arith.evaluate(self, old) if old else 0))
        value = (old or "") + value
        return value.lower() if "l" in f else value.upper() if "u" in f else value

    def _check_writable(self, name: str) -> None:
        if self.has_attr(name, "r"):
            raise ShellError(diagnostic("readonlyVariable", name=name))

    def get_array(self, name: str) -> ShellArray | None:
        """The array called name (through namerefs), including the shell's own FUNCNAME, PIPESTATUS and BASH_VERSINFO."""
        name = self.resolve_name(name)
        a = self.arrays.get(name)
        if a is not None:
            return a
        if name == "FUNCNAME" and self._func_names:
            items = list(reversed(self._func_names))
        elif name == "BASH_SOURCE" and self._sources:
            items = list(reversed(self._sources))
        elif name == "PIPESTATUS":
            items = [str(c) for c in self._pipe_status]
        elif name == "BASH_VERSINFO":
            items = _BASH_VERSINFO
        else:
            return None
        built = ShellArray(False)
        for item in items:
            built.set(str(built.next), item)
        return built

    def _shell_variable(self, name: str) -> str | None:
        """Shell variables bash sets itself; they are not in the environment `env` prints."""
        if name == "IFS":
            return None if self._ifs_unset else " \t\n"
        fixed = {"OPTIND": "1", "BASH": "/usr/bin/bash", "BASH_VERSION": "5.2.21(1)-release", "PPID": "4241", "BASHPID": "4242",
                 "SHLVL": "1", "OSTYPE": "linux-gnu", "MACHTYPE": "x86_64-pc-linux-gnu", "HOSTTYPE": "x86_64"}
        if name in fixed:
            return fixed[name]
        if name in ("UID", "EUID"):
            return "0" if self.user == "root" else "1000"
        if name == "LINENO":
            return str(self._lineno)
        if name == "SECONDS":
            return str(int(time.time() - self._seconds_base))
        if name == "EPOCHSECONDS":
            return str(int(time.time()))
        if name == "EPOCHREALTIME":
            return f"{time.time():.6f}"
        return None

    def get_variable(self, name: str) -> str:
        if name and name[0].isdigit() and name.isdigit():
            n = int(name)
            return self._arg0 if n == 0 else self.positional[n - 1] if n <= len(self.positional) else ""
        if name == "?":
            return str(self.last_exit_code)
        if name == "$":
            return "4242"
        if name == "!":
            return "" if self._last_bg == 0 else str(self._last_bg)
        if name == "#":
            return str(len(self.positional))
        if name == "@":
            return " ".join(self.positional)
        if name == "*":
            return self._ifs_joiner.join(self.positional)
        if name == "-":
            return self._option_flags
        if name == "_":
            return self._last_arg
        if name == "PWD":
            return self._cwd
        if name == "RANDOM":
            return str(random.randrange(32768))
        v = self._look_up(name)
        return "" if v is None else v

    def _look_up(self, name: str) -> str | None:
        """A named variable's value (element 0 of an array), or None when it is unset."""
        name = self.resolve_name(name)
        a = self.get_array(name)
        if a is not None:
            return a.get("0")
        v = self.env.get(name)
        return v if v is not None else self._shell_variable(name)

    @property
    def ifs(self) -> str:
        """The IFS that splitting uses: the default when it is unset."""
        return self.env.get("IFS", " \t\n")

    @property
    def _ifs_joiner(self) -> str:
        """"$*" and "${a[*]}" join with the first character of IFS (a space when IFS is unset)."""
        ifs = self.env.get("IFS")
        return " " if ifs is None else ifs[:1]

    def _is_set(self, name: str) -> bool:
        if name in ("?", "$", "#", "@", "*", "-", "_", "PWD", "RANDOM", "0"):
            return True
        if name and name[0].isdigit():
            return name.isdigit() and int(name) <= len(self.positional)
        if name == "!":
            return self._last_bg != 0
        return self._look_up(name) is not None

    # ---------- arrays ----------

    def writable(self, name: str, assoc: bool = False) -> ShellArray:
        """The array to change: created if missing, copied first if a saved state still shares it."""
        name = self.resolve_name(name)
        self._check_writable(name)
        a = self.arrays.get(name)
        if a is None:
            a = ShellArray(assoc, self._epoch)
            if name in self.env:
                a.set("0", self.env.pop(name))  # a plain variable becomes element 0
            self.arrays[name] = a
            return a
        if a.epoch != self._epoch:
            a = self.arrays[name] = a.clone(self._epoch)
        return a

    def declare_array(self, name: str, assoc: bool, local: bool) -> None:
        """`local arr` / `declare -A m`: a fresh array, restored to the caller's when the function returns."""
        self._check_writable(name)
        if local and self._local_arrays:
            self._local_arrays[-1].setdefault(name, self.arrays.get(name))
        elif name in self.arrays and self.arrays[name].assoc == assoc:
            return
        if local and self._locals:
            self.declare_local(name, None)
        self.env.pop(name, None)
        self.arrays[name] = ShellArray(assoc, self._epoch)

    def array_key(self, array: ShellArray | None, subscript: str, name: str = "") -> str:
        """The storage key for a subscript: the text itself for an associative array, else its arithmetic value."""
        if array is not None and array.assoc:
            return subscript
        i = arith.evaluate(self, subscript)
        if i < 0:
            i += array.next if array is not None else 1
        if i < 0:
            raise ShellError(diagnostic("badArraySubscript", name=name))
        return str(i)

    def get_element(self, name: str, subscript: str) -> str | None:
        """One element, or None if unset. A plain variable answers as element 0."""
        name = self.resolve_name(name)
        a = self.get_array(name)
        if a is not None:
            return a.get(self.array_key(a, subscript, name))
        scalar = self.env.get(name)
        return scalar if scalar is not None and self.array_key(None, subscript, name) == "0" else None

    def set_element(self, name: str, subscript: str, value: str) -> None:
        a = self.writable(name)
        key = self.array_key(a, subscript, name)
        old = a.get(key)
        self.charge_variable(name, len(key) + len(value), len(key) + len(old) if old is not None else 0)
        a.set(key, value)

    def unset(self, target: str, nameref: bool = False) -> None:
        """unset NAME or unset 'NAME[sub]'; nameref unsets a nameref itself, not what it names."""
        if not nameref:
            target = self.resolve_name(target)
        r = array_ref(target)
        if r is not None and not r[0] and r[3] == "" and r[1] in self.arrays and r[2] not in ("@", "*"):
            a = self.writable(r[1])
            a.remove(self.array_key(a, _unquote(r[2]), r[1]))
            return
        name = r[1] if r is not None else target
        if self.has_attr(name, "r"):
            raise ShellError(diagnostic("cannotUnsetReadonly", name=name))
        self.env.pop(name, None)
        self.arrays.pop(name, None)
        self.var_attrs.pop(name, None)
        self._unexported.discard(name)
        if name == "IFS":
            self._ifs_unset = True

    async def _array_elements(self, expr: str, io_: _Io, quoted: bool = False) -> list[str] | None:
        """The elements ${name[@]...} stands for (keys for ${!name[@]}); None if expr is not a whole-array expansion."""
        r = array_ref(expr)
        if r is None or r[2] not in ("@", "*"):
            return None
        keys_, rname, sub, tail = r
        a = self.get_array(rname)
        name = self.resolve_name(rname)
        if a is None:
            scalar = self.env.get(name)
            items = [] if scalar is None else ["0" if keys_ else scalar]
        else:
            items = a.keys() if keys_ else a.values()
        if keys_ or not tail:
            return items
        d = default_op(tail)
        if d is not None:
            op, word = d
            # ${a[@]:-word}: the array as a whole is tested; empty means no elements (or, with ':', nothing but empty text)
            missing = not items or (op[0] == ":" and not (self._ifs_joiner if sub == "*" else " ").join(items))
            if op.endswith("?") and missing:
                msg = await self._param_word_expand(word, quoted, io_) if word else "parameter null or not set"
                raise self._fatal(io_, f"{rname}[{sub}]: {msg}", 127)
            if op.endswith("=") and missing:
                raise ShellError(diagnostic("badArraySubscript", name=f"{rname}[{sub}]"))
            if op.endswith("-"):
                return [await self._param_word_expand(word, quoted, io_)] if missing else items
            if op.endswith("+"):
                return [] if missing else [await self._param_word_expand(word, quoted, io_)]
            return items
        if tail[0] == ":":
            spec = tail[1:].split(":", 1)
            frm = await self._arith(spec[0], io_)
            count = (1 << 62) if len(spec) == 1 else await self._arith(spec[1], io_)
            if count < 0:
                raise ShellError(diagnostic("substringNegative", expression=spec[1].strip()))
            # an indexed array is sliced by index, so a sparse array skips its gaps: offsets count from the first index >= from
            if a is not None and not a.assoc:
                ks = [int(k) for k in a.keys()]
                if frm < 0:
                    frm += 0 if not ks else ks[-1] + 1
                if frm < 0:
                    return []
                first = next((i for i, k in enumerate(ks) if k >= frm), -1)
                return [] if first < 0 else items[first:first + count]
            if frm < 0:
                frm += len(items)
            if frm < 0 or frm > len(items):
                return []
            return items[frm:frm + count]
        # any other operator applies to each element: ${files[@]%.txt}, ${names[@]^^}
        return [await self._element_op(item, tail, io_, quoted) for item in items]

    async def _element_op(self, value: str | None, rest: str, io_: _Io, quoted: bool = False) -> str:
        """Applies a ${...} operator to one value by expanding it through a scratch variable."""
        if not rest:
            return value or ""
        if value is None:
            self.env.pop(_SCRATCH, None)
        else:
            self.env[_SCRATCH] = value
        try:
            return await self._expand_parameter(_SCRATCH + rest, io_, quoted)
        finally:
            self.env.pop(_SCRATCH, None)

    async def expand_subscript(self, target: str, io_: _Io) -> str:
        """`unset 'a[${#a[@]}-1]'`: name[sub] with the subscript's expansions done (the subscript stays text otherwise)."""
        r = array_ref(target)
        if r is not None and not r[0] and r[3] == "" and ("$" in r[2] or "`" in r[2]):
            return f"{r[1]}[{await self._subscript(r[2], io_)}]"
        return target

    async def _subscript(self, sub: str, io_: _Io) -> str:
        if not any(c in sub for c in "$`\"'"):
            return sub
        return await self._expand_single(Word(Lexer(_unquote(sub)).lex_quoted(None)), io_)

    async def _assign_array(self, a: Assignment, io_: _Io) -> None:
        name = self.resolve_name(a.name)
        if a.elements is None:
            key = await self._expand_single(a.index, io_)
            value = await self._expand_assignment(a.value, io_)
            if self.xtrace:
                io_.err.write(f"+ {name}[{key}]{'+=' if a.append else '='}{trace_quote(value)}\n")
            try:
                self.set_var(f"{name}[{key}]", value, a.append)
            except arith.ArithError as e:
                raise self._fatal(io_, str(e), 1) from None
            return
        if a.append:
            target = self.writable(name)
        else:
            self._check_writable(name)
            old = self.arrays.get(name)
            target = ShellArray(old is not None and old.assoc, self._epoch)
            self.env.pop(name, None)
        for w in Parser.parse_words(a.elements):
            # [key]=value sets that element; anything else is appended, after the usual splitting and globbing
            first = w.parts[0] if w.parts else None
            split = split_subscript(w, 1) if isinstance(first, Literal) and not first.quoted and first.text.startswith("[") else None
            if split is not None:
                target.set(self.array_key(target, await self._expand_single(split[0], io_)), await self._expand_single(split[1], io_))
            else:
                for f in await self._expand_words([w], io_):
                    target.set(str(target.next), f)
        self.arrays[name] = target

    async def _declare_arrays(self, words, io_: _Io):
        """`local a=(x y)`, `declare -A m`, `declare -a list=()`: the array arguments are handled here, because a built-in
        only receives expanded text. Returns the words left for the built-in, or None if none were array declarations."""
        flags = "".join(t for t in (w.unquoted_literal for w in words[1:]) if t and t.startswith("-"))
        if any(f in flags for f in "pfF"):
            return None
        local = words[0].is_("local") or (self.in_function and "g" not in flags)
        assoc = "A" in flags
        any_ = False
        attrs = "".join(c for c in flags if c in "rx") + ("r" if words[0].is_("readonly") else "")
        keep = [words[0]]
        for w in words[1:]:
            lit = w.unquoted_literal
            if lit and lit.startswith("-"):
                continue
            assignment = try_assignment(w)
            if assignment is not None and assignment.elements is not None:
                self.declare_array(assignment.name, assoc, local)
                await self._assign_array(assignment, io_)
            elif assignment is None and (assoc or "a" in flags) and lit and is_name(lit):
                self.declare_array(lit, assoc, local)
            else:
                keep.append(w)
                continue
            if attrs:
                self.set_attrs(assignment.name if assignment is not None else lit, attrs)
            any_ = True
        return keep if any_ else None


# ---------- helpers ----------

def array_ref(expr: str):
    """Splits name[sub]rest (or !name[sub] for the keys) into (keys, name, sub, tail); None when not that shape."""
    start = 1 if expr.startswith("!") else 0
    open_ = expr.find("[")
    if open_ <= start or not is_name(expr[start:open_]):
        return None
    depth, k = 0, open_
    while k < len(expr):
        if expr[k] == "[":
            depth += 1
        elif expr[k] == "]":
            depth -= 1
            if depth == 0:
                break
        k += 1
    return (start == 1, expr[start:open_], expr[open_ + 1:k], expr[k + 1:]) if k < len(expr) else None


def _unquote(s: str) -> str:
    return s[1:-1] if len(s) >= 2 and s[0] in "\"'" and s[-1] == s[0] else s
