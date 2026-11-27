"""git: a local version control system over the virtual filesystem (see gitcore.py for the formats). No network, no
remotes; the .git directory it writes is a real repository."""
from __future__ import annotations

import re
import zlib
from datetime import timezone
from typing import TYPE_CHECKING

from .._spec import COMMANDS
from ..vfs import ShellError, normalize
from . import spec_sub_message
from .gitcore import (
    Commit, Config, EMPTY_TREE, Entry, GitError, IgnoreRules, MAX_DEPTH, check_path, fatal, msg, object_id, parse_tree,
    read_index, tree_bytes, write_index,
)
from .gitpack import Corrupt, MAX_NAMES, MAX_PACKS, ObjectCache, Pack

if TYPE_CHECKING:
    from .gitrevisions import Git


SPEC = COMMANDS["git"]


def sub_msg(sub: str, key: str, /, **values) -> str:
    return spec_sub_message("git", sub, key, **values)


def sub_fatal(sub: str, key: str, /, code: int = 128, **values) -> GitError:
    return GitError(sub_msg(sub, key, **values), code)


# ---------- option parsing ----------

class Opts:
    def __init__(self):
        self.flags: dict[str, int] = {}
        self.values: dict[str, list[str]] = {}
        self.args: list[str] = []  # operands before "--"
        self.paths: list[str] = []  # operands after "--"
        self.dashdash = False

    def has(self, k: str) -> bool:
        return k in self.flags

    def val(self, k: str, default=None):
        v = self.values.get(k)
        return v[-1] if v else default

    def vals(self, k: str) -> list[str]:
        return self.values.get(k, [])


def usage_error(sub: str, text: str) -> GitError:
    return GitError(text + "\n" + SPEC.subcommands[sub].help.rstrip("\n"), 129)


def parse(sub: str, args: list[str]) -> Opts:
    flags, valued, longs = SPEC.subcommands[sub].options
    mapping = dict(x.split("=", 1) for x in longs.split(",") if x)
    if sub in ("diff", "log", "show"):  # --color[=when] is listed as custom: accepted and ignored (output is never colored)
        args = [a for a in args if not re.fullmatch(r"--color(=.*)?", a)]
    o = Opts()
    i = 0
    while i < len(args):
        a = args[i]
        i += 1
        if a == "--":
            o.dashdash = True
            o.paths = args[i:]
            break
        if a.startswith("--"):
            name, eq, inline = a[2:].partition("=")
            neg = name.startswith("no-") and name not in mapping and name[3:] in mapping
            key = mapping.get(name[3:] if neg else name)
            if key is None:
                raise usage_error(sub, msg("unknownOption", option=name))
            if neg:
                o.flags.pop(key, None)
                o.values.pop(key, None)
                o.flags["!" + key] = 1
                continue
            if key in valued:
                if not eq:
                    if i >= len(args):
                        raise GitError(msg("optionNeedsValue", option=name), 129)
                    inline = args[i]
                    i += 1
                o.values.setdefault(key, []).append(inline)
                o.flags[key] = o.flags.get(key, 0) + 1
            else:
                if eq:
                    raise GitError(msg("optionTakesNoValue", option=name), 129)
                o.flags[key] = o.flags.get(key, 0) + 1
            continue
        if len(a) < 2 or a[0] != "-":
            o.args.append(a)
            continue
        k = 1
        while k < len(a):
            c = a[k]
            if c in valued:
                v = a[k + 1:]
                if not v:
                    if i >= len(args):
                        raise GitError(msg("switchNeedsValue", option=c), 129)
                    v = args[i]
                    i += 1
                o.values.setdefault(c, []).append(v)
                o.flags[c] = o.flags.get(c, 0) + 1
                break
            if c not in flags:
                raise usage_error(sub, msg("unknownSwitch", option=c))
            o.flags[c] = o.flags.get(c, 0) + 1
            k += 1
    return o


# ---------- the repository ----------

def packed_name_ok(name: str) -> bool:
    """A packed-refs name git could have written: under refs/, the characters loose refs allow, no empty, '.'-led or
    '..' component and no .lock name, so nothing read from the file can name a path outside refs/."""
    return (name.startswith("refs/") and re.fullmatch(r"[A-Za-z0-9_./-]+", name) is not None
            and all(c and not c.startswith(".") and not c.endswith(".lock") for c in name.split("/")))


class Repo:
    def __init__(self, g: "Git", top: str, gitdir: str):
        self.g, self.ctx, self.fs = g, g.ctx, g.ctx.fs
        self.top, self.git = top, gitdir
        self.limit = g.ctx.shell.max_string_bytes
        self._cache = ObjectCache()  # objects, commits and pack delta bases, under one byte budget
        self._packs: list[Pack] | None = None
        self._config = None
        self._ignores: dict[str, IgnoreRules | None] = {}
        self.prefix = ""  # the cwd below the top ("sub/dir/"), for relative paths

    # --- files under .git, never through a symbolic link ---
    def _gpath(self, rel: str) -> str:
        p = self.git
        for part in rel.split("/"):
            if part in ("", ".", ".."):
                raise fatal("invalidPath", path=rel)
            p += "/" + part
            if self.fs.is_symlink(p):
                raise fatal("symlinkInGitDir", path=p)
        return p

    def gread(self, rel: str) -> bytes | None:
        p = self._gpath(rel)
        if not self.fs.is_file(p, follow=False):
            return None
        if self.fs.stat(p).size > self.limit:
            raise fatal("tooLarge", what=p)
        return self.fs.read_bytes(p)

    def gtext(self, rel: str) -> str | None:
        b = self.gread(rel)
        return None if b is None else b.decode("utf-8", "replace")

    def gwrite(self, rel: str, data: bytes) -> None:
        p = self._gpath(rel)
        d = p.rsplit("/", 1)[0]
        if not self.fs.is_dir(d, follow=False):
            self.fs.mkdir(d, parents=True)
        self.fs.write_bytes(p, data)

    def gremove(self, rel: str) -> None:
        p = self._gpath(rel)
        if self.fs.exists(p, follow=False):
            self.fs.delete(p)

    def gexists(self, rel: str) -> bool:
        return self.fs.exists(self._gpath(rel), follow=False)

    # --- objects ---
    def obj(self, sha: str) -> tuple[str, bytes]:
        hit = self._cache.get(sha)
        if hit:
            return hit
        if any(sha in p.offsets for p in self.packs()):  # packs first, as git does: a dictionary lookup, no file walk
            return self._packed(sha)
        raw = self.gread(f"objects/{sha[:2]}/{sha[2:]}") if re.fullmatch(r"[0-9a-f]{40}", sha) else None
        if raw is None:
            return self._packed(sha)
        try:
            d = zlib.decompressobj()
            data = d.decompress(raw, self.limit + 64)
            if d.unconsumed_tail:
                raise fatal("tooLarge", what=f"object {sha}")
        except zlib.error:
            raise fatal("corruptObject", object=sha) from None
        nul = data.find(b"\0")
        head = data[:nul].decode("ascii", "replace").split(" ") if nul > 0 else []
        if len(head) != 2 or head[0] not in ("blob", "tree", "commit", "tag") or not head[1].isdigit() or int(head[1]) != len(data) - nul - 1:
            raise fatal("corruptObject", object=sha)
        result = (head[0], data[nul + 1:])
        if object_id(*result) != sha:
            raise fatal("corruptObject", object=sha)
        self._cache.put(sha, result, len(result[1]))
        return result

    def packs(self) -> list[Pack]:
        """The packs under objects/pack that have an index, loaded and verified on first use."""
        if self._packs is None:
            self._packs = []
            d = "objects/pack"
            names = sorted(n for n in self.fs.listdir(self._gpath(d)) if n.endswith(".idx")) if self.fs.is_dir(self._gpath(d), follow=False) else []
            if len(names) > MAX_PACKS:
                raise fatal("tooLarge", what=self._gpath(d))
            for name in names:
                pack = self.gread(f"{d}/{name[:-4]}.pack")
                if pack is not None:  # like git, an index without its pack is skipped
                    self._packs.append(Pack(self._gpath(f"{d}/{name[:-4]}.pack"), self.gread(f"{d}/{name}") or b"", pack, self.limit,
                                            self.ctx.check_time, self._cache))
                    if sum(len(p.names) for p in self._packs) > MAX_NAMES:
                        raise fatal("tooLarge", what=self._gpath(d))
        return self._packs

    def _packed(self, sha: str) -> tuple[str, bytes]:
        for pack in self.packs():
            if sha in pack.offsets:
                try:
                    result = pack.read(sha)
                except Corrupt:
                    raise fatal("corruptObject", object=sha) from None
                if object_id(*result) != sha:
                    raise fatal("corruptObject", object=sha)
                self._cache.put(sha, result, len(result[1]))
                return result
        raise fatal("missingObject", object=sha)

    def has(self, sha: str) -> bool:
        return any(sha in p.offsets for p in self.packs()) or self.gexists(f"objects/{sha[:2]}/{sha[2:]}")

    def put(self, kind: str, data: bytes) -> str:
        sha = object_id(kind, data)
        if not self.has(sha):
            self.gwrite(f"objects/{sha[:2]}/{sha[2:]}", zlib.compress(f"{kind} {len(data)}\0".encode() + data))
        return sha

    def typed(self, sha: str, kind: str) -> bytes:
        k, data = self.obj(sha)
        if k != kind:
            raise fatal("wrongType", object=sha, kind=kind)
        return data

    def commit(self, sha: str) -> Commit:
        c = self._cache.get(("commit", sha))
        if c:
            return c
        data = self.typed(sha, "commit").decode("utf-8", "surrogateescape")
        head, _, message = data.partition("\n\n")
        tree, parents, author, committer = None, [], "", ""
        for line in head.split("\n"):
            k, _, v = line.partition(" ")
            if k == "tree":
                tree = v
            elif k == "parent":
                parents.append(v)
            elif k == "author":
                author = v
            elif k == "committer":
                committer = v
        if not tree or not re.fullmatch(r"[0-9a-f]{40}", tree) or any(not re.fullmatch(r"[0-9a-f]{40}", p) for p in parents):
            raise fatal("corruptObject", object=sha)
        c = Commit(sha, tree, parents, author, committer, message)
        self._cache.put(("commit", sha), c, len(data) + 200)
        return c

    def tree(self, sha: str):
        return parse_tree(self.typed(sha, "tree"), sha)

    def flatten(self, tree_sha: str | None, prefix: str = "", depth: int = 0, out=None) -> dict[str, tuple[str, str]]:
        """{path: (mode, sha)} for every file and link in a tree."""
        out = {} if out is None else out
        if tree_sha is None or tree_sha == EMPTY_TREE:
            return out
        if depth > MAX_DEPTH:
            raise fatal("tooDeep", object=tree_sha)
        for mode, name, sha in self.tree(tree_sha):
            self.ctx.check_time()
            if mode == "040000":
                self.flatten(sha, prefix + name + "/", depth + 1, out)
            elif mode != "160000":
                out[prefix + name] = (mode, sha)
            if len(out) > 1_000_000:
                raise fatal("tooLarge", what=f"tree {tree_sha}")
        return out

    def write_tree(self, files: dict[str, tuple[str, str]]) -> str:
        root: dict = {}
        for path, (mode, sha) in files.items():
            node = root
            parts = path.split("/")
            for p in parts[:-1]:
                node = node.setdefault(p, {})
                if not isinstance(node, dict):
                    raise fatal("invalidPath", path=path)
            node[parts[-1]] = (mode, sha)

        def build(node) -> str:
            entries = []
            for name, v in node.items():
                if isinstance(v, dict):
                    entries.append(("040000", name, build(v)))
                else:
                    entries.append((v[0], name, v[1]))
            return self.put("tree", tree_bytes(entries))
        return build(root)

    # --- refs ---
    def read_ref(self, name: str, depth: int = 0) -> str | None:
        """The object a ref (HEAD, refs/heads/x, ...) points to, following symbolic refs; None when it doesn't exist."""
        if depth > 5:
            return None
        if not re.fullmatch(r"[A-Za-z0-9_./-]+", name) or ".." in name or any(p in ("", ".") for p in name.split("/")):
            return None  # refs/heads/. (from `git checkout .`) names no ref
        text = self.gtext(name) if not self.fs.is_dir(self._gpath(name), follow=False) else None
        if text is not None:
            text = text.strip()
            if text.startswith("ref:"):
                return self.read_ref(text[4:].strip(), depth + 1)
            return text if re.fullmatch(r"[0-9a-f]{40}", text) else None
        return self.packed_refs().get(name)

    def packed_refs(self) -> dict[str, str]:
        text = self.gtext("packed-refs") or ""
        out = {}
        for line in text.split("\n"):
            parts = line.split(" ")
            if len(parts) == 2 and re.fullmatch(r"[0-9a-f]{40}", parts[0]) and packed_name_ok(parts[1]):
                out[parts[1]] = parts[0]
        return out

    def head_ref(self) -> str | None:
        """The branch HEAD names (refs/heads/x), or None when detached."""
        text = (self.gtext("HEAD") or "").strip()
        if not text.startswith("ref:"):
            return None
        target = text[4:].strip()
        from .gitrevisions import valid_head_target
        if not valid_head_target(target):
            raise fatal("badSymbolicRef", path="HEAD", target=target)
        return target

    def head(self) -> str | None:
        return self.read_ref("HEAD")

    def branch(self) -> str | None:
        r = self.head_ref()
        return r[len("refs/heads/"):] if r and r.startswith("refs/heads/") else r

    def update_ref(self, name: str, sha: str, log: str | None = None) -> None:
        if name == "HEAD" and self.head_ref():
            name = self.head_ref()
        old = self.read_ref(name) or "0" * 40
        self.gwrite(name, (sha + "\n").encode())
        if log is not None:
            self.reflog(name, old, sha, log)
            if self.head_ref() == name:
                self.reflog("HEAD", old, sha, log)
        elif name == "HEAD":
            pass

    def reflog(self, name: str, old: str, new: str, message: str) -> None:
        who = self.g.ident("committer", required=False)
        line = f"{old} {new} {who}\t{message}\n".encode("utf-8", "surrogateescape")
        rel = "logs/" + name
        prev = self.gread(rel) or b""
        self.gwrite(rel, prev + line)

    def set_head(self, target: str, log: str | None = None, detach: bool = False) -> None:
        """Points HEAD at a branch (refs/heads/x) or, detached, at a commit."""
        old = self.head() or "0" * 40
        self.gwrite("HEAD", (target + "\n" if detach else f"ref: {target}\n").encode())
        if log is not None:
            self.reflog("HEAD", old, target if detach else (self.read_ref(target) or "0" * 40), log)

    def delete_ref(self, name: str) -> None:
        self.gremove(name)
        self.gremove("logs/" + name)
        packed = self.gtext("packed-refs")
        if packed and f" {name}" in packed:
            lines = packed.split("\n")
            out, skip = [], False
            for line in lines:
                if skip and line.startswith("^"):
                    continue
                skip = line.endswith(" " + name)
                if not skip:
                    out.append(line)
            self.gwrite("packed-refs", "\n".join(out).encode())

    def refs(self, prefix: str) -> list[tuple[str, str]]:
        """[(name, sha)] under prefix (refs/heads/), loose and packed, sorted by name."""
        found = {k: v for k, v in self.packed_refs().items() if k.startswith(prefix)}
        base = self._gpath(prefix.rstrip("/"))
        if self.fs.is_dir(base, follow=False):
            stack = [prefix.rstrip("/")]
            while stack:
                rel = stack.pop()
                for name in self.fs.listdir(self._gpath(rel)):
                    self.ctx.check_time()
                    p = rel + "/" + name
                    full = self._gpath(p)
                    if self.fs.is_dir(full, follow=False):
                        stack.append(p)
                    elif self.fs.is_file(full, follow=False):
                        v = (self.gtext(p) or "").strip()
                        if re.fullmatch(r"[0-9a-f]{40}", v):
                            found[p] = v
                    if len(found) > 100_000:
                        raise fatal("tooLarge", what="refs")
        return sorted(found.items(), key=lambda kv: kv[0].encode())

    # --- the index ---
    def index(self) -> list[Entry]:
        data = self.gread("index")
        if data is None:
            return []
        return read_index(data, 1_000_000)

    def save_index(self, entries) -> None:
        self.gwrite("index", write_index(list(entries)))

    def stage0(self, entries=None) -> dict[str, Entry]:
        return {e.path: e for e in (self.index() if entries is None else entries) if e.stage == 0}

    def unmerged(self, entries=None) -> dict[str, dict[int, Entry]]:
        out: dict[str, dict[int, Entry]] = {}
        for e in (self.index() if entries is None else entries):
            if e.stage:
                out.setdefault(e.path, {})[e.stage] = e
        return out

    # --- config ---
    def config(self) -> Config:
        if self._config is None:
            self._config = Config(self.gtext("config") or "")
        return self._config

    def save_config(self) -> None:
        self.gwrite("config", self.config().text().encode())

    # --- the work tree ---
    def wt(self, rel: str) -> str:
        return self.top.rstrip("/") + "/" + rel if rel else self.top

    def wt_reachable(self, rel: str) -> bool:
        """Whether rel's parent directories are real directories (git never looks through a symbolic link)."""
        p = self.top.rstrip("/")
        for k, part in enumerate(rel.split("/")[:-1]):
            p += "/" + part
            if k % 32 == 31:
                self.ctx.check_time()
            if not self.fs.is_dir(p, follow=False):
                return False
        return True

    def wt_stat(self, rel: str):
        """(mode, data) of a work tree file or link, or None when there is none."""
        if not self.wt_reachable(rel):
            return None
        p = self.wt(rel)
        if self.fs.is_symlink(p):
            return "120000", self.fs.read_link(p).encode("utf-8", "surrogateescape")
        if not self.fs.is_file(p, follow=False):
            return None
        st = self.fs.stat(p)
        if st.size > self.limit:
            raise fatal("tooLarge", what=rel)
        return ("100755" if st.mode & 0o100 else "100644"), self.fs.read_bytes(p)

    def wt_id(self, rel: str):
        """(mode, blob id) of a work tree path, or None."""
        s = self.wt_stat(rel)
        return None if s is None else (s[0], object_id("blob", s[1]))

    def wt_write(self, rel: str, mode: str, sha: str) -> None:
        check_path(rel)
        data = self.typed(sha, "blob")
        p = self.top
        for part in rel.split("/")[:-1]:
            p += "/" + part
            if self.fs.is_symlink(p) or (self.fs.exists(p, follow=False) and not self.fs.is_dir(p, follow=False)):
                self.fs.delete(p)
            if not self.fs.exists(p, follow=False):
                self.fs.mkdir(p)
        full = self.wt(rel)
        if self.fs.is_symlink(full) or self.fs.is_dir(full, follow=False):
            self.fs.delete(full, recursive=True)
        if mode == "120000":
            if self.fs.exists(full, follow=False):
                self.fs.delete(full)
            self.fs.create_symlink(full, data.decode("utf-8", "surrogateescape"))
            return
        self.fs.write_bytes(full, data)
        want = (0o777 if mode == "100755" else 0o666) & ~self.ctx.shell.umask
        if self.fs.stat(full).mode & 0o777 != want:
            self.fs.chmod(full, want)

    def wt_remove(self, rel: str) -> None:
        if not self.wt_reachable(rel):
            return
        full = self.wt(rel)
        if self.fs.exists(full, follow=False) and not self.fs.is_dir(full, follow=False):
            self.fs.delete(full)
        # empty parent directories go too
        parts = rel.split("/")[:-1]
        while parts:
            d = self.wt("/".join(parts))
            if self.g.cwd == d or self.g.cwd.startswith(d + "/"):
                break  # the directory git runs in stays
            if self.fs.is_dir(d, follow=False) and not self.fs.listdir(d):
                self.fs.delete(d)
            else:
                break
            parts.pop()

    def entry_for(self, rel: str, mode: str, sha: str) -> Entry:
        p = self.wt(rel)
        try:
            st = self.fs.stat(p, follow=False)
            return Entry(rel, sha, mode, 0, int(st.modified.replace(tzinfo=timezone.utc).timestamp()), st.size)
        except ShellError:
            return Entry(rel, sha, mode)

    def walk(self, base: str = "", untracked_only_in=None):
        """Every file and link under base (relative to the top), not descending into .git, symbolic links or nested
        repositories. Yields (rel, is_dir_marker): nested repositories come back as 'dir/' entries."""
        stack = [base]
        count = 0
        while stack:
            rel = stack.pop()
            full = self.wt(rel)
            try:
                names = sorted(self.fs.listdir(full))
            except ShellError:
                continue
            for name in names:
                count += 1
                if count % 256 == 0:
                    self.ctx.check_time()
                if name == ".git":
                    continue
                r = rel + "/" + name if rel else name
                p = self.wt(r)
                if self.fs.is_dir(p, follow=False):
                    if self.fs.exists(p + "/.git", follow=False):
                        yield r + "/"
                    else:
                        stack.append(r)
                else:
                    yield r

    def ignore_rules(self, d: str) -> IgnoreRules | None:
        if d not in self._ignores:
            rules = None
            p = self.wt((d + "/" if d else "") + ".gitignore")
            if self.wt_reachable((d + "/" if d else "") + ".gitignore") and self.fs.is_file(p, follow=False) and self.fs.stat(p).size <= 1 << 20:
                rules = IgnoreRules(self.fs.read_bytes(p).decode("utf-8", "replace"), d)
            self._ignores[d] = rules
        return self._ignores[d]

    def exclude_rules(self) -> list[IgnoreRules]:
        if "\0exclude" not in self._ignores:
            rules = []
            t = self.gtext("info/exclude")
            if t:
                rules.append(IgnoreRules(t, ""))
            self._ignores["\0exclude"] = rules  # type: ignore[assignment]
        return self._ignores["\0exclude"]  # type: ignore[return-value]

    def ignored(self, rel: str, is_dir: bool = False) -> bool:
        """Whether an untracked path is ignored, by .gitignore files from its own directory up, then info/exclude. A path
        inside an ignored directory is ignored too."""
        parts = rel.split("/")
        for i in range(1, len(parts)):
            if self._ignored_here("/".join(parts[:i]), True):
                return True
        return self._ignored_here(rel, is_dir)

    def _ignored_here(self, rel: str, is_dir: bool) -> bool:
        parts = rel.split("/")
        for i in range(len(parts) - 1, -1, -1):
            r = self.ignore_rules("/".join(parts[:i]))
            if r:
                m = r.match(rel, is_dir)
                if m is not None:
                    return m
        for r in self.exclude_rules():
            m = r.match(rel, is_dir)
            if m is not None:
                return m
        return False

    # --- paths as the user gives and sees them ---
    def from_user(self, path: str) -> str:
        """A path the user typed (relative to the cwd) -> relative to the top; error when outside the repository."""
        full = normalize(self.g.cwd, path)
        if full == self.top:
            return ""
        top = self.top.rstrip("/")
        if not full.startswith(top + "/"):
            raise fatal("outsideRepo", path=path, top=self.top)
        return full[len(top) + 1:]

    def show(self, rel: str) -> str:
        """rel (from the top) as seen from the cwd."""
        if not self.prefix:
            return rel
        pre = self.prefix.rstrip("/").split("/")
        trailing = rel.endswith("/")
        parts = rel.rstrip("/").split("/")
        k = 0
        while k < len(pre) and k < len(parts) and pre[k] == parts[k]:
            k += 1
        out = "/".join([".."] * (len(pre) - k) + parts[k:])
        return (out + "/" if trailing and out else out) or "./"
