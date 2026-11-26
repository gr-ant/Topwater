using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using static Topwater.GitCore;
using FileMap = System.Collections.Generic.Dictionary<string, (string Mode, string Sha)>;

namespace Topwater;

/// <summary>git: a local version control system over the virtual filesystem (see GitCore.cs for the formats). No network,
/// no remotes; the .git directory it writes is a real repository. A port of python/src/topwater/commands/git.py.</summary>
internal static partial class Git
{
    static CommandSpec.Entry Spec => CommandSpec.Of("git");
    static CommandSpec.SubEntry SubSpec(string sub) => Spec.Subcommands![sub];

    static string SubMsg(string sub, string key, params (string, string)[] values) => CommandSpec.SubMessage("git", sub, key, values);
    static GitException SubFatal(string sub, string key, params (string, string)[] values) => new(SubMsg(sub, key, values), 128);

    static bool Full(string pattern, string text) => Regex.IsMatch(text, @"\A(?:" + pattern + @")\z");

    // ---------- option parsing ----------

    sealed class Opts
    {
        public readonly Dictionary<string, int> Flags = new(StringComparer.Ordinal);
        public readonly Dictionary<string, List<string>> Values = new(StringComparer.Ordinal);
        public readonly List<string> Args = [];  // operands before "--"
        public List<string> Paths = [];  // operands after "--"
        public bool DashDash;

        public bool Has(string k) => Flags.ContainsKey(k);
        public string? Val(string k, string? fallback = null) => Values.TryGetValue(k, out var v) && v.Count > 0 ? v[^1] : fallback;
        public List<string> Vals(string k) => Values.TryGetValue(k, out var v) ? v : [];
        public List<string> Operands => [.. Args, .. Paths];
    }

    static GitException UsageError(string sub, string text) => new(text + "\n" + SubSpec(sub).Help.TrimEnd('\n'), 129);

    static Opts Parse(string sub, IEnumerable<string> argList)
    {
        var (flags, valued, longs) = SubSpec(sub).Options;
        var mapping = longs.Split(',').Where(x => x != "").Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => x[1], StringComparer.Ordinal);
        var args = argList.ToList();
        if (sub is "diff" or "log" or "show")  // --color[=when] is listed as custom: accepted and ignored (output is never colored)
            args = args.Where(a => !Regex.IsMatch(a, @"^--color(=.*)?\z")).ToList();
        var o = new Opts();
        int i = 0;
        while (i < args.Count)
        {
            var a = args[i++];
            if (a == "--")
            {
                o.DashDash = true;
                o.Paths = args[i..];
                break;
            }
            if (a.StartsWith("--"))
            {
                var body = a[2..];
                int eqAt = body.IndexOf('=');
                bool eq = eqAt >= 0;
                string name = eq ? body[..eqAt] : body, inline = eq ? body[(eqAt + 1)..] : "";
                bool neg = name.StartsWith("no-") && !mapping.ContainsKey(name) && mapping.ContainsKey(name[3..]);
                if (!mapping.TryGetValue(neg ? name[3..] : name, out var key)) throw UsageError(sub, Msg("unknownOption", ("option", name)));
                if (neg)
                {
                    o.Flags.Remove(key);
                    o.Values.Remove(key);
                    o.Flags["!" + key] = 1;
                    continue;
                }
                if (valued.Contains(key))
                {
                    if (!eq)
                    {
                        if (i >= args.Count) throw new GitException(Msg("optionNeedsValue", ("option", name)), 129);
                        inline = args[i++];
                    }
                    Add(o, key, inline);
                }
                else
                {
                    if (eq) throw new GitException(Msg("optionTakesNoValue", ("option", name)), 129);
                    o.Flags[key] = o.Flags.GetValueOrDefault(key) + 1;
                }
                continue;
            }
            if (a.Length < 2 || a[0] != '-')
            {
                o.Args.Add(a);
                continue;
            }
            for (int k = 1; k < a.Length; k++)
            {
                var c = a[k].ToString();
                if (valued.Contains(c))
                {
                    var v = a[(k + 1)..];
                    if (v.Length == 0)
                    {
                        if (i >= args.Count) throw new GitException(Msg("switchNeedsValue", ("option", c)), 129);
                        v = args[i++];
                    }
                    Add(o, c, v);
                    break;
                }
                if (!flags.Contains(c)) throw UsageError(sub, Msg("unknownSwitch", ("option", c)));
                o.Flags[c] = o.Flags.GetValueOrDefault(c) + 1;
            }
        }
        return o;
    }

    static void Add(Opts o, string key, string value)
    {
        if (!o.Values.TryGetValue(key, out var list)) o.Values[key] = list = [];
        list.Add(value);
        o.Flags[key] = o.Flags.GetValueOrDefault(key) + 1;
    }

    // ---------- objects ----------

    sealed class Commit(string sha, string tree, List<string> parents, string author, string committer, string message)
    {
        public readonly string Sha = sha, Tree = tree, Author = author, Committer = committer, Message = message;
        public readonly List<string> Parents = parents;

        public string Subject
        {
            get
            {
                var m = Message.TrimStart('\n');
                int k = m.IndexOf("\n\n", StringComparison.Ordinal);
                return string.Join(" ", (k >= 0 ? m[..k] : m).Split('\n')).Trim();
            }
        }

        public string Body
        {
            get
            {
                var m = Message.TrimStart('\n');
                int k = m.IndexOf("\n\n", StringComparison.Ordinal);
                return k >= 0 ? m[(k + 2)..].TrimStart('\n') : "";
            }
        }
    }

    sealed class Change(string st, string? old, string? @new)
    {
        public string St = st;
        public string? Old = old, New = @new;
        public string Path => (New ?? Old)!;
    }

    sealed record Stat(string St, string? Old, string? New, (int Ins, int Dels)? N, long SA, long SB);

    // ---------- the repository ----------

    sealed class Repo
    {
        public readonly Cmd G;
        public readonly CommandContext Ctx;
        public readonly VirtualFileSystem Fs;
        public readonly string Top, GitDir;
        public readonly long Limit;
        readonly Dictionary<string, (string, byte[])> _objects = [];
        readonly Dictionary<string, Commit> _commits = [];
        Config? _config;
        readonly Dictionary<string, IgnoreRules?> _ignores = [];
        List<IgnoreRules>? _exclude;
        public string Prefix = "";  // the cwd below the top ("sub/dir/"), for relative paths
        public bool NoIgnore;  // clean -x: nothing counts as ignored

        public Repo(Cmd g, string top, string gitdir)
        {
            G = g; Ctx = g.Ctx; Fs = g.Ctx.Fs;
            Top = top; GitDir = gitdir;
            Limit = g.Ctx.Shell.Options.MaxStringBytes;
        }

        public void Check() => Ctx.CancellationToken.ThrowIfCancellationRequested();

        // --- files under .git, never through a symbolic link ---
        public string GPath(string rel)
        {
            var p = GitDir;
            foreach (var part in rel.Split('/'))
            {
                if (part is "" or "." or "..") throw Fatal("invalidPath", ("path", rel));
                p += "/" + part;
                if (Fs.IsSymlink(p)) throw Fatal("symlinkInGitDir", ("path", p));
            }
            return p;
        }

        public byte[]? GRead(string rel)
        {
            var p = GPath(rel);
            if (!Fs.IsFile(p, false)) return null;
            if (Fs.Stat(p).Size > Limit) throw Fatal("tooLarge", ("what", p));
            return Fs.ReadAllBytes(p);
        }

        public string? GText(string rel) => GRead(rel) is { } b ? Encoding.UTF8.GetString(b) : null;

        public void GWrite(string rel, byte[] data)
        {
            var p = GPath(rel);
            var d = p[..p.LastIndexOf('/')];
            if (!Fs.IsDirectory(d, false)) Fs.CreateDirectory(d, true);
            Fs.WriteAllBytes(p, data);
        }

        public void GWrite(string rel, string text) => GWrite(rel, ByteText.Encode(text));

        public void GRemove(string rel)
        {
            var p = GPath(rel);
            if (Fs.Exists(p, false)) Fs.Delete(p);
        }

        public bool GExists(string rel) => Fs.Exists(GPath(rel), false);

        // --- objects ---
        public (string Kind, byte[] Data) Obj(string sha)
        {
            if (_objects.TryGetValue(sha, out var hit)) return hit;
            var raw = Sha.IsMatch(sha) ? GRead($"objects/{sha[..2]}/{sha[2..]}") : null;
            if (raw == null)
            {
                if (GExists("objects/pack") && Fs.IsDirectory(GPath("objects/pack")) && Fs.List(GPath("objects/pack")).Any(n => n.EndsWith(".pack")))
                    throw Fatal("packedObjects", ("object", sha));
                throw Fatal("missingObject", ("object", sha));
            }
            byte[] data;
            try
            {
                using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                var ms = new MemoryStream();
                var buf = new byte[81920];
                long max = Limit + 64;
                int n;
                while ((n = z.Read(buf, 0, buf.Length)) > 0)
                {
                    ms.Write(buf, 0, n);
                    if (ms.Length > max) throw Fatal("tooLarge", ("what", $"object {sha}"));
                }
                data = ms.ToArray();
            }
            catch (InvalidDataException) { throw Fatal("corruptObject", ("object", sha)); }
            int nul = Array.IndexOf(data, (byte)0);
            var head = nul > 0 ? Encoding.ASCII.GetString(data, 0, nul).Split(' ') : [];
            if (head.Length != 2 || head[0] is not ("blob" or "tree" or "commit" or "tag") || !Regex.IsMatch(head[1], @"^[0-9]+\z")
                || !long.TryParse(head[1], out var size) || size != data.Length - nul - 1)
                throw Fatal("corruptObject", ("object", sha));
            var result = (head[0], data[(nul + 1)..]);
            if (ObjectId(result.Item1, result.Item2) != sha) throw Fatal("corruptObject", ("object", sha));
            if (_objects.Count < 4096) _objects[sha] = result;
            return result;
        }

        public bool Has(string sha) => GExists($"objects/{sha[..2]}/{sha[2..]}");

        public string Put(string kind, byte[] data)
        {
            var sha = ObjectId(kind, data);
            var rel = $"objects/{sha[..2]}/{sha[2..]}";
            if (!GExists(rel))
            {
                var ms = new MemoryStream();
                using (var z = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(Header(kind, data));
                GWrite(rel, ms.ToArray());
            }
            return sha;
        }

        public string Put(string kind, string text) => Put(kind, ByteText.Encode(text));

        public byte[] Typed(string sha, string kind)
        {
            var (k, data) = Obj(sha);
            if (k != kind) throw Fatal("wrongType", ("object", sha), ("kind", kind));
            return data;
        }

        public Commit Commit(string sha)
        {
            if (_commits.TryGetValue(sha, out var c)) return c;
            var data = ByteText.Decode(Typed(sha, "commit"));
            int k = data.IndexOf("\n\n", StringComparison.Ordinal);
            string head = k >= 0 ? data[..k] : data, message = k >= 0 ? data[(k + 2)..] : "";
            string? tree = null;
            string author = "", committer = "";
            var parents = new List<string>();
            foreach (var line in head.Split('\n'))
            {
                int sp = line.IndexOf(' ');
                string key = sp >= 0 ? line[..sp] : line, v = sp >= 0 ? line[(sp + 1)..] : "";
                switch (key)
                {
                    case "tree": tree = v; break;
                    case "parent": parents.Add(v); break;
                    case "author": author = v; break;
                    case "committer": committer = v; break;
                }
            }
            if (string.IsNullOrEmpty(tree) || !Sha.IsMatch(tree) || parents.Any(p => !Sha.IsMatch(p))) throw Fatal("corruptObject", ("object", sha));
            c = new Commit(sha, tree, parents, author, committer, message);
            _commits[sha] = c;
            return c;
        }

        public List<(string Mode, string Name, string Sha)> Tree(string sha) => ParseTree(Typed(sha, "tree"), sha);

        /// <summary>{path: (mode, sha)} for every file and link in a tree.</summary>
        public FileMap Flatten(string? treeSha, string prefix = "", int depth = 0, FileMap? output = null)
        {
            output ??= new FileMap(StringComparer.Ordinal);
            if (treeSha == null || treeSha == EmptyTree) return output;
            if (depth > MaxDepth) throw Fatal("tooDeep", ("object", treeSha));
            foreach (var (mode, name, sha) in Tree(treeSha))
            {
                Check();
                if (mode == "040000") Flatten(sha, prefix + name + "/", depth + 1, output);
                else if (mode != "160000") output[prefix + name] = (mode, sha);
                if (output.Count > 1_000_000) throw Fatal("tooLarge", ("what", $"tree {treeSha}"));
            }
            return output;
        }

        sealed class Node { public readonly Dictionary<string, object> Children = new(StringComparer.Ordinal); }

        public string WriteTree(FileMap files)
        {
            var root = new Node();
            foreach (var (path, id) in files)
            {
                var node = root;
                var parts = path.Split('/');
                if (parts.Length > MaxDepth) CheckPath(path);  // bounds Build's recursion below
                foreach (var p in parts[..^1])
                {
                    if (!node.Children.TryGetValue(p, out var next)) node.Children[p] = next = new Node();
                    node = next as Node ?? throw Fatal("invalidPath", ("path", path));
                }
                node.Children[parts[^1]] = id;
            }
            string Build(Node node)
            {
                var entries = new List<(string, string, string)>();
                foreach (var (name, v) in node.Children)
                    entries.Add(v is Node n ? ("040000", name, Build(n)) : (((string Mode, string Sha))v).Mode is var m ? (m, name, (((string Mode, string Sha))v).Sha) : default);
                return Put("tree", TreeBytes(entries));
            }
            return Build(root);
        }

        // --- refs ---
        /// <summary>The object a ref (HEAD, refs/heads/x, ...) points to, following symbolic refs; null when it doesn't exist.</summary>
        public string? ReadRef(string name, int depth = 0)
        {
            if (depth > 5) return null;
            if (!Regex.IsMatch(name, @"^[A-Za-z0-9_./-]+\z") || name.Contains("..") || name.StartsWith('/') || name.EndsWith('/')) return null;
            var text = !Fs.IsDirectory(GPath(name), false) ? GText(name) : null;
            if (text != null)
            {
                text = text.Trim();
                if (text.StartsWith("ref:")) return ReadRef(text[4..].Trim(), depth + 1);
                return Sha.IsMatch(text) ? text : null;
            }
            return PackedRefs().GetValueOrDefault(name);
        }

        public Dictionary<string, string> PackedRefs()
        {
            var output = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in (GText("packed-refs") ?? "").Split('\n'))
            {
                var parts = line.Split(' ');
                if (parts.Length == 2 && Sha.IsMatch(parts[0])) output[parts[1]] = parts[0];
            }
            return output;
        }

        /// <summary>The branch HEAD names (refs/heads/x), or null when detached.</summary>
        public string? HeadRef()
        {
            var text = (GText("HEAD") ?? "").Trim();
            if (!text.StartsWith("ref:")) return null;
            var target = text[4..].Trim();
            if (!ValidHeadTarget(target)) throw Fatal("badSymbolicRef", ("path", "HEAD"), ("target", target));
            return target;
        }

        public string? Head() => ReadRef("HEAD");

        public string? Branch()
        {
            var r = HeadRef();
            return r != null && r.StartsWith("refs/heads/") ? r["refs/heads/".Length..] : r;
        }

        public void UpdateRef(string name, string sha, string? log = null)
        {
            if (name == "HEAD" && HeadRef() is { } hr) name = hr;
            var old = ReadRef(name) ?? new string('0', 40);
            GWrite(name, sha + "\n");
            if (log != null)
            {
                Reflog(name, old, sha, log);
                if (HeadRef() == name) Reflog("HEAD", old, sha, log);
            }
        }

        public void Reflog(string name, string old, string @new, string message)
        {
            var who = G.Ident("committer", required: false);
            var line = ByteText.Encode($"{old} {@new} {who}\t{message}\n");
            var rel = "logs/" + name;
            GWrite(rel, [.. GRead(rel) ?? [], .. line]);
        }

        /// <summary>Points HEAD at a branch (refs/heads/x) or, detached, at a commit.</summary>
        public void SetHead(string target, string? log = null, bool detach = false)
        {
            var old = Head() ?? new string('0', 40);
            GWrite("HEAD", detach ? target + "\n" : $"ref: {target}\n");
            if (log != null) Reflog("HEAD", old, detach ? target : ReadRef(target) ?? new string('0', 40), log);
        }

        public void DeleteRef(string name)
        {
            GRemove(name);
            GRemove("logs/" + name);
            var packed = GText("packed-refs");
            if (!string.IsNullOrEmpty(packed) && packed.Contains(" " + name))
            {
                var output = new List<string>();
                bool skip = false;
                foreach (var line in packed.Split('\n'))
                {
                    if (skip && line.StartsWith('^')) continue;
                    skip = line.EndsWith(" " + name);
                    if (!skip) output.Add(line);
                }
                GWrite("packed-refs", string.Join("\n", output));
            }
        }

        /// <summary>[(name, sha)] under prefix (refs/heads/), loose and packed, sorted by name.</summary>
        public List<(string Name, string Sha)> Refs(string prefix)
        {
            var found = PackedRefs().Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(StringComparer.Ordinal);
            var top = prefix.TrimEnd('/');
            if (Fs.IsDirectory(GPath(top), false))
            {
                var stack = new List<string> { top };
                while (stack.Count > 0)
                {
                    var rel = stack[^1];
                    stack.RemoveAt(stack.Count - 1);
                    foreach (var name in Fs.List(GPath(rel)))
                    {
                        Check();
                        var p = rel + "/" + name;
                        var full = GPath(p);
                        if (Fs.IsDirectory(full, false)) stack.Add(p);
                        else if (Fs.IsFile(full, false))
                        {
                            var v = (GText(p) ?? "").Trim();
                            if (Sha.IsMatch(v)) found[p] = v;
                        }
                        if (found.Count > 100_000) throw Fatal("tooLarge", ("what", "refs"));
                    }
                }
            }
            return found.Select(kv => (kv.Key, kv.Value)).OrderBy(kv => kv.Key, ByBytes).ToList();
        }

        // --- the index ---
        public List<Entry> Index() => GRead("index") is { } data ? ReadIndex(data, 1_000_000) : [];

        public void SaveIndex(IEnumerable<Entry> entries) => GWrite("index", WriteIndex(entries));

        public Dictionary<string, Dictionary<int, Entry>> Unmerged(List<Entry>? entries = null)
        {
            var output = new Dictionary<string, Dictionary<int, Entry>>(StringComparer.Ordinal);
            foreach (var e in entries ?? Index())
                if (e.Stage != 0)
                {
                    if (!output.TryGetValue(e.Path, out var st)) output[e.Path] = st = [];
                    st[e.Stage] = e;
                }
            return output;
        }

        public Dictionary<string, Entry> Stage0(List<Entry>? entries = null)
        {
            var output = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var e in entries ?? Index()) if (e.Stage == 0) output[e.Path] = e;
            return output;
        }

        // --- config ---
        public Config Config() => _config ??= new Config(GText("config") ?? "");

        public void SaveConfig() => GWrite("config", Encoding.UTF8.GetBytes(Config().Text()));

        // --- the work tree ---
        public string Wt(string rel) => rel.Length > 0 ? Top.TrimEnd('/') + "/" + rel : Top;

        /// <summary>Whether rel's parent directories are real directories (git never looks through a symbolic link).</summary>
        public bool WtReachable(string rel)
        {
            var p = Top.TrimEnd('/');
            var parts = rel.Split('/');
            for (int k = 0; k < parts.Length - 1; k++)
            {
                p += "/" + parts[k];
                if (k % 32 == 31) Check();
                if (!Fs.IsDirectory(p, false)) return false;
            }
            return true;
        }

        /// <summary>(mode, data) of a work tree file or link, or null when there is none.</summary>
        public (string Mode, byte[] Data)? WtStat(string rel)
        {
            if (!WtReachable(rel)) return null;
            var p = Wt(rel);
            if (Fs.IsSymlink(p)) return ("120000", ByteText.Encode(Fs.ReadLink(p)));
            if (!Fs.IsFile(p, false)) return null;
            var st = Fs.Stat(p);
            if (st.Size > Limit) throw Fatal("tooLarge", ("what", rel));
            return ((st.Mode & 0x40) != 0 ? "100755" : "100644", Fs.ReadAllBytes(p));
        }

        /// <summary>(mode, blob id) of a work tree path, or null.</summary>
        public (string Mode, string Sha)? WtId(string rel) => WtStat(rel) is { } s ? (s.Mode, ObjectId("blob", s.Data)) : null;

        public void WtWrite(string rel, (string Mode, string Sha) t) => WtWrite(rel, t.Mode, t.Sha);

        public void WtWrite(string rel, string mode, string sha)
        {
            CheckPath(rel);
            var data = Typed(sha, "blob");
            var p = Top;
            var parts = rel.Split('/');
            foreach (var part in parts[..^1])
            {
                p += "/" + part;
                if (Fs.IsSymlink(p) || (Fs.Exists(p, false) && !Fs.IsDirectory(p, false))) Fs.Delete(p);
                if (!Fs.Exists(p, false)) Fs.CreateDirectory(p);
            }
            var full = Wt(rel);
            if (Fs.IsSymlink(full) || Fs.IsDirectory(full, false)) Fs.Delete(full, recursive: true);
            if (mode == "120000")
            {
                if (Fs.Exists(full, false)) Fs.Delete(full);
                Fs.CreateSymlink(full, ByteText.Decode(data));
                return;
            }
            Fs.WriteAllBytes(full, data);
            int want = (mode == "100755" ? 0x1FF : 0x1B6) & ~Fs.Umask;
            if ((Fs.Stat(full).Mode & 0x1FF) != want) Fs.Chmod(full, want);
        }

        public void WtRemove(string rel)
        {
            if (!WtReachable(rel)) return;
            var full = Wt(rel);
            if (Fs.Exists(full, false) && !Fs.IsDirectory(full, false)) Fs.Delete(full);
            // empty parent directories go too
            var parts = rel.Split('/').ToList();
            parts.RemoveAt(parts.Count - 1);
            while (parts.Count > 0)
            {
                var d = Wt(string.Join("/", parts));
                if (G.Cwd == d || G.Cwd.StartsWith(d + "/", StringComparison.Ordinal)) break;  // the directory git runs in stays
                if (Fs.IsDirectory(d, false) && Fs.List(d).Count == 0) Fs.Delete(d);
                else break;
                parts.RemoveAt(parts.Count - 1);
            }
        }

        public Entry EntryFor(string rel, (string Mode, string Sha) t) => EntryFor(rel, t.Mode, t.Sha);

        public Entry EntryFor(string rel, string mode, string sha)
        {
            try
            {
                var st = Fs.Stat(Wt(rel), false);
                return new Entry(rel, sha, mode, 0, Epoch(st.Modified), st.Size);
            }
            catch (ShellException) { return new Entry(rel, sha, mode); }
        }

        IReadOnlyList<string>? ListOrNull(string path)
        {
            try { return Fs.List(path); }
            catch (ShellException) { return null; }
        }

        /// <summary>Every file and link under base (relative to the top), not descending into .git, symbolic links or nested
        /// repositories: those come back as 'dir/' entries.</summary>
        public IEnumerable<string> Walk(string @base = "")
        {
            var stack = new List<string> { @base };
            int count = 0;
            while (stack.Count > 0)
            {
                var rel = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                var names = ListOrNull(Wt(rel));
                if (names == null) continue;
                foreach (var name in names.Order(GitCore.Ordinal).ToList())
                {
                    if (++count % 256 == 0) Check();
                    if (name == ".git") continue;
                    var r = rel.Length > 0 ? rel + "/" + name : name;
                    var p = Wt(r);
                    if (Fs.IsDirectory(p, false))
                    {
                        if (Fs.Exists(p + "/.git", false)) yield return r + "/";
                        else stack.Add(r);
                    }
                    else yield return r;
                }
            }
        }

        public IReadOnlyList<string>? List(string path) => ListOrNull(path);

        IgnoreRules? IgnoreRulesFor(string d)
        {
            if (!_ignores.TryGetValue(d, out var rules))
            {
                var rel = (d.Length > 0 ? d + "/" : "") + ".gitignore";
                var p = Wt(rel);
                if (WtReachable(rel) && Fs.IsFile(p, false) && Fs.Stat(p).Size <= 1 << 20)
                    rules = new IgnoreRules(Encoding.UTF8.GetString(Fs.ReadAllBytes(p)), d);
                _ignores[d] = rules;
            }
            return rules;
        }

        List<IgnoreRules> ExcludeRules()
        {
            if (_exclude == null)
            {
                _exclude = [];
                var t = GText("info/exclude");
                if (!string.IsNullOrEmpty(t)) _exclude.Add(new IgnoreRules(t, ""));
            }
            return _exclude;
        }

        /// <summary>Whether an untracked path is ignored, by .gitignore files from its own directory up, then info/exclude.
        /// A path inside an ignored directory is ignored too.</summary>
        public bool Ignored(string rel, bool isDir = false)
        {
            if (NoIgnore) return false;
            var parts = rel.Split('/');
            for (int i = 1; i < parts.Length; i++)
                if (IgnoredHere(string.Join("/", parts[..i]), true)) return true;
            return IgnoredHere(rel, isDir);
        }

        bool IgnoredHere(string rel, bool isDir)
        {
            var parts = rel.Split('/');
            for (int i = parts.Length - 1; i >= 0; i--)
                if (IgnoreRulesFor(string.Join("/", parts[..i])) is { } r && r.Match(rel, isDir) is { } m) return m;
            foreach (var r in ExcludeRules())
                if (r.Match(rel, isDir) is { } m) return m;
            return false;
        }

        // --- paths as the user gives and sees them ---
        /// <summary>A path the user typed (relative to the cwd) -> relative to the top; error when outside the repository.</summary>
        public string FromUser(string path)
        {
            var full = VirtualFileSystem.Normalize(G.Cwd, path);
            if (full == Top) return "";
            var top = Top.TrimEnd('/');
            if (!full.StartsWith(top + "/", StringComparison.Ordinal)) throw Fatal("outsideRepo", ("path", path), ("top", Top));
            return full[(top.Length + 1)..];
        }

        /// <summary>rel (from the top) as seen from the cwd.</summary>
        public string Show(string rel)
        {
            if (Prefix.Length == 0) return rel;
            var pre = Prefix.TrimEnd('/').Split('/');
            bool trailing = rel.EndsWith('/');
            var parts = rel.TrimEnd('/').Split('/');
            int k = 0;
            while (k < pre.Length && k < parts.Length && pre[k] == parts[k]) k++;
            var output = string.Join("/", Enumerable.Repeat("..", pre.Length - k).Concat(parts[k..]));
            var shown = trailing && output.Length > 0 ? output + "/" : output;
            return shown.Length > 0 ? shown : "./";
        }
    }
}
