using System.Text;
using System.Text.RegularExpressions;
using static Topwater.GitCore;
using FileMap = System.Collections.Generic.Dictionary<string, (string Mode, string Sha)>;

namespace Topwater;

internal static partial class Git
{
    // ---------- revisions ----------

    /// <summary>A revision -> object id. Throws badRevision when it names nothing.</summary>
    static string Resolve(Repo repo, string spec)
    {
        var sha = TryResolve(repo, spec);
        if (sha == null)
        {
            MissingPath(repo, spec);
            throw Fatal("badRevision", ("rev", spec));
        }
        return sha;
    }

    /// <summary>rev:path where rev exists but path does not gets its own message.</summary>
    static void MissingPath(Repo repo, string spec)
    {
        int c = spec.IndexOf(':');
        if (c > 0 && TryResolve(repo, spec[..c]) != null) throw Fatal("pathNotInRevision", ("path", spec[(c + 1)..]), ("rev", spec[..c]));
    }

    static readonly Regex RevSuffix = new(@"^(.*?)((?:[~^][0-9]*|\^\{\w*\})*)\z", RegexOptions.Singleline);
    static readonly Regex RevOps = new(@"~[0-9]*|\^\{\w*\}|\^[0-9]*");

    static string? TryResolve(Repo repo, string spec)
    {
        if (spec.Length == 0) return null;
        if (spec.Contains(':') && !spec.StartsWith(":/"))
        {
            int c = spec.IndexOf(':');
            string rev = spec[..c], path = spec[(c + 1)..];
            if (rev.Length == 0) return repo.Stage0().GetValueOrDefault(path.TrimStart('/'))?.Sha;
            var tree = Peel(repo, TryResolve(repo, rev), "tree");
            if (tree == null) return null;
            if (path is "" or ".") return tree;
            var p = path.StartsWith("./") ? path[2..] : path;
            if (path.StartsWith("./") || path.StartsWith("../"))
            {
                try { p = repo.FromUser(path); }
                catch (GitException) { return null; }
            }
            return LookupPath(repo, tree, p.Trim('/'));
        }
        var m = RevSuffix.Match(spec);
        string? sha = ResolveBase(repo, m.Groups[1].Value);
        if (sha == null) return null;
        foreach (Match opm in RevOps.Matches(m.Groups[2].Value))
        {
            var op = opm.Value;
            if (op.StartsWith("^{"))
            {
                var want = op[2..^1];
                sha = Peel(repo, sha, want.Length > 0 ? want : "any");
            }
            else if (op[0] == '~')
            {
                long n = op.Length > 1 ? (long.TryParse(op[1..], out var x) ? x : long.MaxValue) : 1;
                for (long i = 0; i < n; i++)
                {
                    var c = Peel(repo, sha, "commit");
                    if (c == null) return null;
                    var ps = repo.Commit(c).Parents;
                    sha = ps.Count > 0 ? ps[0] : null;
                    if (sha == null) return null;
                    repo.Check();
                }
            }
            else
            {
                long n = op.Length > 1 ? (long.TryParse(op[1..], out var x) ? x : long.MaxValue) : 1;
                var c = Peel(repo, sha, "commit");
                if (c == null) return null;
                if (n == 0) { sha = c; continue; }
                var ps = repo.Commit(c).Parents;
                sha = ps.Count >= n ? ps[(int)n - 1] : null;
            }
            if (sha == null) return null;
        }
        return sha;
    }

    static bool IsUpper(string s) => s.Any(char.IsUpper) && !s.Any(char.IsLower);

    static string? ResolveBase(Repo repo, string name)
    {
        if (name is "@" or "HEAD" or "") return name.Length > 0 ? repo.Head() : null;
        var m = Regex.Match(name, @"^@\{-([0-9]+)\}\z");
        if (m.Success)
        {
            var b = int.TryParse(m.Groups[1].Value, out var n) ? PreviousBranch(repo, n) : null;
            return b != null ? repo.ReadRef("refs/heads/" + b) : null;
        }
        foreach (var r in new[] { name, "refs/" + name, "refs/tags/" + name, "refs/heads/" + name, "refs/remotes/" + name })
        {
            if (r == name && !(IsUpper(name) || name.StartsWith("refs/"))) continue;
            if (repo.ReadRef(r) is { } sha) return sha;
        }
        if (Hex.IsMatch(name)) return name.Length == 40 ? (repo.Has(name) ? name : null) : UniquePrefix(repo, name);
        return null;
    }

    static string? UniquePrefix(Repo repo, string prefix)
    {
        var d = repo.GPath($"objects/{prefix[..2]}");
        if (!repo.Fs.IsDirectory(d, false)) return null;
        var hits = repo.Fs.List(d).Select(n => prefix[..2] + n).Where(x => x.StartsWith(prefix, StringComparison.Ordinal) && x.Length == 40).ToList();
        if (hits.Count > 1) throw Fatal("ambiguousPrefix", ("prefix", prefix));
        return hits.Count > 0 ? hits[0] : null;
    }

    static string? PreviousBranch(Repo repo, int n)
    {
        var lines = (repo.GText("logs/HEAD") ?? "").Split('\n');
        int seen = 0;
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var m = Regex.Match(lines[i], @"\tcheckout: moving from (\S+) to (\S+)$");
            if (m.Success && ++seen == n) return m.Groups[1].Value;
        }
        return null;
    }

    /// <summary>Follows tags (and commit -> tree) until an object of kind want ('commit', 'tree', 'any' = not a tag).</summary>
    static string? Peel(Repo repo, string? sha, string want)
    {
        for (int i = 0; i < 16; i++)
        {
            if (sha == null) return null;
            var (kind, data) = repo.Obj(sha);
            if (kind == want || (want == "any" && kind != "tag")) return sha;
            if (want == "tag") return null;
            if (kind == "tag")
            {
                var m = Regex.Match(Encoding.Latin1.GetString(data), "^object ([0-9a-f]{40})");
                sha = m.Success ? m.Groups[1].Value : null;
            }
            else if (kind == "commit" && want == "tree") sha = repo.Commit(sha).Tree;
            else return null;
        }
        return null;
    }

    static string? LookupPath(Repo repo, string tree, string path)
    {
        var sha = tree;
        foreach (var part in path.Split('/').Where(p => p.Length > 0))
        {
            if (repo.Obj(sha).Kind != "tree") return null;
            var found = repo.Tree(sha).Where(e => e.Name == part).ToList();
            if (found.Count == 0) return null;
            sha = found[0].Sha;
        }
        return sha;
    }

    static string CommitOf(Repo repo, string spec) => Peel(repo, Resolve(repo, spec), "commit") ?? throw Fatal("notCommit", ("rev", spec));

    static FileMap TreeFiles(Repo repo, string? commit) => commit != null ? repo.Flatten(repo.Commit(commit).Tree) : new FileMap(StringComparer.Ordinal);

    static HashSet<string> Ancestors(Repo repo, IEnumerable<string> start)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = start.ToList();
        while (stack.Count > 0)
        {
            var c = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            if (!seen.Add(c)) continue;
            if (seen.Count > MaxWalk) throw Fatal("tooLarge", ("what", "history"));
            if (seen.Count % 256 == 0) repo.Check();
            stack.AddRange(repo.Commit(c).Parents);
        }
        return seen;
    }

    /// <summary>A best common ancestor: a shared ancestor that is not itself an ancestor of another shared one (newest if several).</summary>
    static string? MergeBase(Repo repo, string a, string b)
    {
        var common = Ancestors(repo, [a]);
        common.IntersectWith(Ancestors(repo, [b]));
        if (common.Count == 0) return null;
        var below = Ancestors(repo, common.SelectMany(c => repo.Commit(c).Parents));
        var best = common.Where(c => !below.Contains(c)).Order(GitCore.Ordinal).ToList();
        return best.Count > 0 ? WalkOrder(repo, best)[0] : null;
    }

    /// <summary>Commits reachable from starts, newest commit date first (ties: first seen first), each once.</summary>
    static List<string> WalkOrder(Repo repo, IEnumerable<string> starts)
    {
        var heap = new PriorityQueue<string, (long, long)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var output = new List<string>();
        long counter = 0;
        foreach (var s in starts)
            if (seen.Add(s)) heap.Enqueue(s, (-CommitTime(repo, s), counter++));
        while (heap.TryDequeue(out var c, out _))
        {
            output.Add(c);
            if (output.Count > MaxWalk) throw Fatal("tooLarge", ("what", "history"));
            if (output.Count % 256 == 0) repo.Check();
            foreach (var p in repo.Commit(c).Parents)
                if (seen.Add(p)) heap.Enqueue(p, (-CommitTime(repo, p), counter++));
        }
        return output;
    }

    static long CommitTime(Repo repo, string sha) => ParseIdent(repo.Commit(sha).Committer).Epoch;

    // ---------- the command ----------

    /// <summary>One git invocation: the shell context, the directory it works from (after -C) and -c overrides.</summary>
    sealed class Cmd(CommandContext ctx, string cwd, List<(string, string)> overrides)
    {
        public readonly CommandContext Ctx = ctx;
        public readonly string Cwd = cwd;
        public readonly List<(string Key, string Value)> Overrides = overrides;
        Repo? _repo;
        public Dictionary<string, List<string>>? Decor;

        public void Out(string s) => Ctx.Stdout.Write(s);
        public void Err(string s) => Ctx.Stderr.Write(s);
        public void Check() => Ctx.CancellationToken.ThrowIfCancellationRequested();

        public string? Env(string name) =>
            Ctx.Shell.Environment.TryGetValue(name, out var v) && Ctx.Shell.IsExported(name) ? v : null;

        public Repo? FindRepo()
        {
            if (_repo == null)
            {
                var fs = Ctx.Fs;
                var d = Cwd;
                while (true)
                {
                    var g = d.TrimEnd('/') + "/.git";
                    if (fs.IsDirectory(g, false) && fs.IsFile(g + "/HEAD", false) && ValidHead(fs, g + "/HEAD"))
                    {
                        var top = d.TrimEnd('/');
                        _repo = new Repo(this, top.Length > 0 ? top : "/", g);
                        var rel = Cwd[top.Length..].Trim('/');
                        _repo.Prefix = rel.Length > 0 ? rel + "/" : "";
                        break;
                    }
                    if (fs.IsSymlink(g)) throw Fatal("symlinkInGitDir", ("path", g));
                    if (d == "/") break;
                    var cut = d[..d.LastIndexOf('/')];
                    d = cut.Length > 0 ? cut : "/";
                }
            }
            return _repo;
        }

        public Repo Repo() => FindRepo() ?? throw Fatal("notRepo");

        public (string Path, Config Config) GlobalConfig()
        {
            var path = VirtualFileSystem.Normalize("/", (Env("HOME") ?? "/") + "/.gitconfig");
            var fs = Ctx.Fs;
            var text = fs.IsFile(path) && fs.Stat(path).Size <= 1 << 20 ? Encoding.UTF8.GetString(fs.ReadAllBytes(path)) : "";
            return (path, new Config(text));
        }

        public string? Config(string name)
        {
            for (int i = Overrides.Count - 1; i >= 0; i--) if (Overrides[i].Key == name) return Overrides[i].Value;
            if (FindRepo() is { } r && r.Config().Get(name) is { } v) return v;
            return GlobalConfig().Config.Get(name);
        }

        public DateTime Now => Ctx.Shell.Now;

        static string? Truthy(string? s) => string.IsNullOrEmpty(s) ? null : s;

        public string Ident(string kind, bool required = true, string? name = null, string? email = null, string? date = null)
        {
            var k = kind.ToUpperInvariant();
            var user = Ctx.Shell.Options.User;
            name = Truthy(name) ?? Truthy(Env($"GIT_{k}_NAME")) ?? Truthy(Config("user.name")) ?? user;
            email ??= Truthy(Env($"GIT_{k}_EMAIL")) ?? Truthy(Config("user.email")) ?? Env("EMAIL");
            if (email == null)
            {
                var guess = $"{user}@{Ctx.Shell.Options.Hostname}.(none)";
                if (required) throw new GitException(Msg("identityUnknown", ("guess", guess)), 128);
                email = guess;
            }
            var when = Truthy(date) ?? Truthy(Env($"GIT_{k}_DATE"));
            (name, email) = (CleanIdent(name), CleanIdent(email));
            if (name.Length == 0 && required) throw new GitException(Msg("emptyIdentName", ("email", email)), 128);
            long epoch;
            string zone;
            if (when != null)
            {
                var parsed = ParseDate(when, Now) ?? throw new GitException(Msg("badDate", ("date", when)), 128);
                (epoch, zone) = parsed;
            }
            else (epoch, zone) = (GitCore.Epoch(Now), "+0000");
            return $"{name} <{email}> {epoch} {zone}";
        }

        /// <summary>No message was given: with GIT_EDITOR / core.editor / VISUAL / EDITOR set to true or :, keep the existing
        /// message (amend, merge); otherwise say why no editor can open.</summary>
        public string EditorError(string? existing = null)
        {
            var editor = Truthy(Env("GIT_EDITOR")) ?? Truthy(Config("core.editor")) ?? Truthy(Env("VISUAL")) ?? Env("EDITOR");
            if (editor != null && editor.Trim() is "true" or ":" && existing != null) return existing;
            throw new GitException(SubMsg("commit", !string.IsNullOrEmpty(editor) ? "editorCannotRun" : "noEditor"), 1);
        }
    }

    /// <summary>Newlines and angle brackets would break the commit header, so they go, as in git.</summary>
    static string CleanIdent(string s) => new string(s.Where(c => c is not ('<' or '>' or '\n' or '\r')).ToArray()).Trim();

    static bool ValidHeadTarget(string target) => target.StartsWith("refs/") && target.Split('/').All(part => part is not ("" or "." or ".."));

    /// <summary>A HEAD that names neither a commit nor a ref under refs/ makes the directory not a repository, as in git.</summary>
    static bool ValidHead(VirtualFileSystem fs, string path)
    {
        if (fs.Stat(path).Size > 4096) return false;
        var text = Encoding.UTF8.GetString(fs.ReadAllBytes(path)).Trim();
        return Sha.IsMatch(text) || (text.StartsWith("ref:") && ValidHeadTarget(text[4..].Trim()));
    }

    public static void Register(Shell shell) => shell.Register("git", Run);

    static int Run(CommandContext ctx)
    {
        var args = ctx.Args.ToList();
        var cwd = ctx.Shell.Cwd;
        var overrides = new List<(string, string)>();
        int i = 0;
        while (i < args.Count && args[i].StartsWith('-'))
        {
            var a = args[i];
            if (a is "--version" or "-v") { ctx.Stdout.Write(Spec.Version + "\n"); return 0; }
            if (a is "--help" or "-h") { ctx.Stdout.Write(Spec.Help); return 0; }
            if (a is "-C" or "-c" && i + 1 >= args.Count)
            {
                ctx.Stderr.Write(Msg("globalNeedsValue", ("option", a)) + "\n");
                return 129;
            }
            if (a == "-C")
            {
                var path = args[i + 1];
                if (path.Length > 0)
                {
                    var target = VirtualFileSystem.Normalize(cwd, path);
                    if (!ctx.Fs.IsDirectory(target))
                    {
                        ctx.Stderr.Write(Msg("cannotChdir", ("path", path)) + "\n");
                        return 128;
                    }
                    cwd = target;
                }
                i += 2;
            }
            else if (a == "-c")
            {
                var kv = args[i + 1];
                int eq = kv.IndexOf('=');
                var k = eq >= 0 ? kv[..eq] : kv;
                if (!ValidKey(k))
                {
                    ctx.Stderr.Write(Msg("badConfigKey", ("key", k)) + "\n");
                    return 129;
                }
                overrides.Add((CanonicalKey(k), eq >= 0 ? kv[(eq + 1)..] : "true"));
                i += 2;
            }
            else if (a is "--no-pager" or "-P" or "--paginate" or "-p" or "--no-replace-objects" or "--literal-pathspecs" or "--no-optional-locks") i++;
            else
            {
                ctx.Stderr.Write(Msg("unknownGlobalOption", ("option", a)) + "\n" + Msg("globalUsage") + "\n");
                return 129;
            }
        }
        if (i >= args.Count) { ctx.Stdout.Write(Spec.Help); return 1; }
        var sub = args[i];
        var rest = args[(i + 1)..];
        Spec.Subcommands!.TryGetValue(sub, out var s);
        int dd = rest.IndexOf("--");
        if (s != null && rest.Take(dd >= 0 ? dd : rest.Count).Contains("--help")) { ctx.Stdout.Write(s.Help); return 0; }
        if (sub == "version") { ctx.Stdout.Write(Spec.Version + "\n"); return 0; }
        if (sub == "help")
        {
            var topic = rest.FirstOrDefault(a => !a.StartsWith('-'));
            if (topic == null) { ctx.Stdout.Write(Spec.Help); return 0; }
            if (!Spec.Subcommands!.TryGetValue(topic, out var t))
            {
                ctx.Stderr.Write(Msg("noHelp", ("name", topic)) + "\n");
                return 1;
            }
            ctx.Stdout.Write(t.Help);
            return 0;
        }
        if (s == null)
        {
            ctx.Stderr.Write(Msg("unknownCommand", ("name", sub)) + "\n");
            return 1;
        }
        if (s.Unsupported)
        {
            ctx.Stderr.Write(SubMsg(sub, "unsupported", ("name", sub)) + "\n");
            return 128;
        }
        if (rest is ["-h"]) { ctx.Stdout.Write(s.Help); return 129; }
        var g = new Cmd(ctx, cwd, overrides);
        try
        {
            return Handlers[sub](g, rest);
        }
        catch (RegexMatchTimeoutException e)
        {
            ctx.Stderr.Write(CommandSpec.Message("git", "patternTimeout", ("pattern", e.Pattern)) + "\n");
            return 128;
        }
        catch (GitException e)
        {
            var text = e.Message;
            if (text.Length > 0) ctx.Stderr.Write(text.EndsWith('\n') ? text : text + "\n");
            return e.Code;
        }
        catch (ShellException e)
        {
            ctx.Stderr.Write(Msg("ioError", ("error", e.Message)) + "\n");
            return 128;
        }
    }

    static readonly Dictionary<string, Func<Cmd, List<string>, int>> Handlers = new(StringComparer.Ordinal)
    {
        ["remote"] = GitRemote, ["init"] = GitInit, ["status"] = GitStatus, ["add"] = GitAdd, ["rm"] = GitRm, ["mv"] = GitMv, ["commit"] = GitCommit, ["log"] = GitLog,
        ["show"] = GitShow, ["diff"] = GitDiff, ["branch"] = GitBranch, ["checkout"] = GitCheckout, ["switch"] = GitSwitch, ["restore"] = GitRestore,
        ["reset"] = GitReset, ["tag"] = GitTag, ["merge"] = GitMerge, ["stash"] = GitStash, ["rev-parse"] = GitRevParse, ["ls-files"] = GitLsFiles,
        ["cat-file"] = GitCatFile, ["config"] = GitConfig, ["clean"] = GitClean, ["rev-list"] = GitRevList,
    };

    // ---------- shared pieces: comparing trees, the index and the work tree ----------

    /// <summary>specs are paths from the top ('' = everything); a spec matches itself, anything below it, or as a glob.</summary>
    static bool PathspecMatch(IReadOnlyList<string>? specs, string path)
    {
        if (specs == null) return true;
        foreach (var s in specs)
        {
            if (s.Length == 0 || path == s || path.StartsWith(s.TrimEnd('/') + "/", StringComparison.Ordinal)) return true;
            if (IsGlob(s) && GlobMatch(s, path)) return true;
        }
        return false;
    }

    static bool IsGlob(string s) => s.IndexOfAny(['*', '?', '[']) >= 0;

    static bool GlobMatch(string glob, string path)
    {
        try { return Glob.Cached(@"\A(?:" + GlobRegex(glob, false) + @")\z", RegexOptions.Singleline | RegexOptions.CultureInvariant).IsMatch(path); }
        catch (ArgumentException) { return false; }
    }

    static List<string>? UserSpecs(Repo repo, List<string> items) => items.Count == 0 ? null : items.Select(repo.FromUser).ToList();

    static string Kind(string mode) => mode == "120000" ? "l" : "f";

    /// <summary>[(status, old path, new path)] between two {path: (mode, sha)} maps, exact renames paired, sorted by path.</summary>
    /// <remarks>A removed and an added path with the same contents pair up as a rename ("R"); with load(side, path) -> bytes,
    /// so do ones at least half alike ("R075": the similarity in percent).</remarks>
    static List<Change> DiffFiles(FileMap a, FileMap b, IReadOnlyList<string>? specs = null, Func<string, string, byte[]>? load = null)
    {
        var output = new List<Change>();
        foreach (var p in a.Keys.Union(b.Keys).Order(ByBytes))
        {
            if (!PathspecMatch(specs, p)) continue;
            bool hx = a.TryGetValue(p, out var x), hy = b.TryGetValue(p, out var y);
            if (!hx) output.Add(new Change("A", null, p));
            else if (!hy) output.Add(new Change("D", p, null));
            else if (x != y) output.Add(new Change(Kind(x.Mode) != Kind(y.Mode) ? "T" : "M", p, p));
        }
        var added = new Dictionary<(string, string), List<Change>>();
        foreach (var r in output.Where(r => r.St == "A"))
        {
            if (!added.TryGetValue(b[r.New!], out var l)) added[b[r.New!]] = l = [];
            l.Add(r);
        }
        foreach (var r in output.Where(r => r.St == "D").ToList())
            if (added.TryGetValue(a[r.Old!], out var cands) && cands.Count > 0)
            {
                var n = cands[0];
                cands.RemoveAt(0);
                n.St = "R";
                n.Old = r.Old;
                output.Remove(r);
            }
        if (load != null)
        {
            var gone = output.Where(r => r.St == "D").ToList();
            var fresh = output.Where(r => r.St == "A").ToList();
            if (gone.Count > 0 && fresh.Count > 0 && (long)gone.Count * fresh.Count <= RenamePairs)
            {
                var src = gone.ToDictionary(r => r.Old!, r => ChunkCounts(load("a", r.Old!)), StringComparer.Ordinal);
                var dst = fresh.ToDictionary(r => r.New!, r => ChunkCounts(load("b", r.New!)), StringComparer.Ordinal);
                var scores = new List<(int S, Change R, Change N)>();
                foreach (var r in gone)
                    foreach (var n in fresh)
                        if (Similarity(src[r.Old!], dst[n.New!]) is var s and >= 50) scores.Add((s, r, n));
                var used = new HashSet<Change>(ReferenceEqualityComparer.Instance);
                foreach (var (s, r, n) in scores.OrderByDescending(x => x.S).ThenBy(x => x.N.New, ByBytes).ThenBy(x => x.R.Old, ByBytes))
                {
                    if (used.Contains(r) || used.Contains(n)) continue;
                    used.Add(r);
                    used.Add(n);
                    n.St = $"R{s:000}";
                    n.Old = r.Old;
                    output.Remove(r);
                }
            }
        }
        return output;
    }

    const long RenamePairs = 10_000;  // removed x added paths compared for inexact renames

    /// <summary>Content split into pieces that end at a newline or after 64 bytes, counted, for rename similarity.</summary>
    static (Dictionary<string, int> Counts, int Length) ChunkCounts(byte[] data)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        int i = 0;
        while (i < data.Length)
        {
            int nl = Array.IndexOf(data, (byte)'\n', i, Math.Min(64, data.Length - i));
            int end = nl >= 0 ? nl + 1 : Math.Min(i + 64, data.Length);
            var piece = Encoding.Latin1.GetString(data, i, end - i);
            counts[piece] = counts.GetValueOrDefault(piece) + 1;
            i = end;
        }
        return (counts, data.Length);
    }

    static int Similarity((Dictionary<string, int> Counts, int Length) x, (Dictionary<string, int> Counts, int Length) y)
    {
        int nx = x.Length, ny = y.Length;
        if (Math.Max(nx, ny) == 0 || (long)Math.Min(nx, ny) * 2 < Math.Max(nx, ny)) return 0;
        long copied = x.Counts.Sum(kv => (long)Math.Min(kv.Value, y.Counts.GetValueOrDefault(kv.Key)) * kv.Key.Length);
        return (int)(copied * 100 / Math.Max(nx, ny));
    }

    static int Score(string st) => st.Length > 1 ? int.Parse(st[1..]) : 100;

    static Func<string, string, byte[]> TreeLoader(Repo repo, FileMap a, FileMap b) => (side, p) => repo.Typed((side == "a" ? a : b)[p].Sha, "blob");

    static FileMap WorktreeFiles(Repo repo, IEnumerable<string> paths)
    {
        var output = new FileMap(StringComparer.Ordinal);
        foreach (var p in paths)
            if (repo.WtId(p) is { } w) output[p] = w;
        return output;
    }

    static FileMap IndexFiles(IEnumerable<Entry> entries)
    {
        var output = new FileMap(StringComparer.Ordinal);
        foreach (var e in entries) if (e.Stage == 0) output[e.Path] = (e.Mode, e.Sha);
        return output;
    }

    sealed class Status
    {
        public readonly Repo Repo;
        public readonly string? Head;
        public readonly List<Entry> Entries;
        public Dictionary<string, Dictionary<int, Entry>> Unmerged;
        public List<Change> Staged, Unstaged;
        public List<string> Untracked;

        public Status(Repo repo, string untrackedMode = "normal")
        {
            Repo = repo;
            Head = repo.Head();
            Entries = repo.Index();
            var idx = IndexFiles(Entries);
            Unmerged = repo.Unmerged(Entries);
            var headFiles = TreeFiles(repo, Head);
            Staged = DiffFiles(headFiles, idx, load: TreeLoader(repo, headFiles, idx)).Where(c => !Unmerged.ContainsKey(c.New ?? c.Old!)).ToList();
            var work = WorktreeFiles(repo, idx.Keys);
            Unstaged = [];
            foreach (var p in idx.Keys.Order(ByBytes))
            {
                if (Unmerged.ContainsKey(p)) continue;
                if (!work.TryGetValue(p, out var w)) Unstaged.Add(new Change("D", p, null));
                else if (w != idx[p]) Unstaged.Add(new Change(Kind(w.Mode) != Kind(idx[p].Mode) ? "T" : "M", p, p));
            }
            var tracked = idx.Keys.Union(Unmerged.Keys).ToHashSet(StringComparer.Ordinal);
            Untracked = untrackedMode != "no" ? Git.Untracked(repo, tracked, untrackedMode) : [];
        }
    }

    static List<string> Untracked(Repo repo, HashSet<string> tracked, string mode)
    {
        var dirs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in tracked)
        {
            var parts = p.Split('/');
            for (int k = 1; k < parts.Length; k++) dirs.Add(string.Join("/", parts[..k]));
        }
        var output = new List<string>();
        int count = 0;
        bool HasFiles(string rel) => repo.Walk(rel).Any(p => !repo.Ignored(p.TrimEnd('/'), p.EndsWith('/')));
        var pending = new Stack<string>([""]);  // a stack, not recursion: directories can nest deeper than the call stack allows
        while (pending.Count > 0)
        {
            var rel = pending.Pop();
            var names = repo.List(repo.Wt(rel));
            if (names == null) continue;
            foreach (var name in names.Order(ByBytes).ToList())
            {
                if (++count % 256 == 0) repo.Check();
                if (name == ".git") continue;
                var r = rel.Length > 0 ? rel + "/" + name : name;
                var full = repo.Wt(r);
                if (repo.Fs.IsDirectory(full, false))
                {
                    if (repo.Ignored(r, true) || tracked.Contains(r)) continue;
                    if (repo.Fs.Exists(full + "/.git", false) && !dirs.Contains(r)) output.Add(r + "/");
                    else if (dirs.Contains(r) || mode == "all") pending.Push(r);
                    else if (HasFiles(r)) output.Add(r + "/");
                }
                else if (!tracked.Contains(r) && !repo.Ignored(r, false)) output.Add(r);
            }
        }
        return output.Order(ByBytes).ToList();
    }

    static byte[] BlobData(Repo repo, (string Mode, string Sha)? side, string path, bool fromWorktree)
    {
        if (side is not { } s) return [];
        if (fromWorktree) return repo.WtStat(path)?.Data ?? [];
        return repo.Typed(s.Sha, "blob");
    }

    static string Abbrev(string sha) => sha.Length > 7 ? sha[..7] : sha;

    /// <summary>'a => b', or with the shared directories factored out: 'dir/{a => b}/f'.</summary>
    static string RenameLabel(string a, string b)
    {
        int pre = 0, n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            if (a[i] != b[i]) break;
            if (a[i] == '/') pre = i + 1;
        }
        int suf = 0;
        for (int i = 1; i < n - pre + 1; i++)
        {
            if (a[^i] != b[^i]) break;
            if (a[^i] == '/') suf = i;
        }
        if (pre > 0 || suf > 0) return $"{a[..pre]}{{{a[pre..(a.Length - suf)]} => {b[pre..(b.Length - suf)]}}}{a[(a.Length - suf)..]}";
        return $"{a} => {b}";
    }

    static (string Mode, string Sha)? Get(FileMap m, string? p) => p != null && m.TryGetValue(p, out var v) ? v : null;

    /// <summary>Prints the differences between two {path: (mode, sha)} maps in the format opts asks for; true when any.</summary>
    static bool WriteDiff(Cmd g, Repo repo, FileMap a, FileMap b, Opts opts, IReadOnlyList<string>? specs = null, bool bWorktree = false, bool aWorktree = false, Action<string>? output = null)
    {
        var write = output ?? g.Out;
        var changes = DiffFiles(a, b, specs, (side, p) => BlobData(repo, (side == "a" ? a : b)[p], p, side == "a" ? aWorktree : bWorktree));
        if (changes.Count == 0) return false;
        if (opts.Has("\x05")) return true;  // --quiet
        if (opts.Has("\x03"))  // --name-only
        {
            foreach (var c in changes) write(CQuote(c.New ?? c.Old!) + "\n");
            return true;
        }
        if (opts.Has("\x04"))  // --name-status
        {
            foreach (var c in changes) write(c.St[0] == 'R' ? $"R{Score(c.St):000}\t{CQuote(c.Old!)}\t{CQuote(c.New!)}\n" : $"{c.St}\t{CQuote(c.New ?? c.Old!)}\n");
            return true;
        }
        var stats = new List<Stat>();
        foreach (var c in changes)
        {
            var x = BlobData(repo, Get(a, c.Old), c.Old ?? "", aWorktree);
            var y = BlobData(repo, Get(b, c.New), c.New ?? "", bWorktree);
            if (IsBinary(x) || IsBinary(y)) stats.Add(new Stat(c.St, c.Old, c.New, null, x.Length, y.Length));
            else stats.Add(new Stat(c.St, c.Old, c.New, x.AsSpan().SequenceEqual(y) ? (0, 0) : CountChanges(x, y, g.Check), x.Length, y.Length));
        }
        if (opts.Has("\x06"))  // --numstat
        {
            foreach (var s in stats)
            {
                var name = s.St[0] == 'R' ? $"{s.Old} => {s.New}" : s.New ?? s.Old!;
                write((s.N is { } n ? $"{n.Ins}\t{n.Dels}\t" : "-\t-\t") + name + "\n");
            }
            return true;
        }
        if (opts.Has("\x01") || opts.Has("\x02") || opts.Has("\x1f"))  // --stat / --shortstat / --summary
        {
            if (opts.Has("\x01") || opts.Has("\x02")) WriteStat(write, stats, shortStat: !opts.Has("\x01"));
            if (opts.Has("\x1f")) foreach (var line in SummaryLines(changes, a, b)) write(line + "\n");
            return true;
        }
        var u = opts.Val("U", "3")!;
        int context = Regex.IsMatch(u, @"^[0-9]+\z") ? (int.TryParse(u, out var cx) ? Math.Min(cx, 100_000_000) : 100_000_000) : 3;
        foreach (var c in changes)
        {
            if (c.St == "T")  // a file became a link or the other way: shown as a deletion and an addition
            {
                WritePatch(g, repo, write, "D", c.Old, null, Get(a, c.Old), null, context, aWorktree, bWorktree);
                WritePatch(g, repo, write, "A", null, c.New, null, Get(b, c.New), context, aWorktree, bWorktree);
                continue;
            }
            WritePatch(g, repo, write, c.St, c.Old, c.New, Get(a, c.Old), Get(b, c.New), context, aWorktree, bWorktree);
        }
        return true;
    }

    static void WriteStat(Action<string> write, List<Stat> stats, bool shortStat = false)
    {
        int files = stats.Count;
        long ins = stats.Sum(s => (long)(s.N?.Ins ?? 0)), dels = stats.Sum(s => (long)(s.N?.Dels ?? 0));
        if (!shortStat)
        {
            var names = stats.Select(s => s.St[0] == 'R' ? RenameLabel(s.Old!, s.New!) : CQuote(s.New ?? s.Old!)).ToList();
            var counts = stats.Select(s => s.N is { } n ? (n.Ins + n.Dels).ToString() : "Bin").ToList();
            int cw = counts.Max(c => c.Length);
            int maxChange = stats.Any(s => s.N != null) ? stats.Where(s => s.N != null).Max(s => s.N!.Value.Ins + s.N!.Value.Dels) : 0;
            int nw = names.Max(n => n.Length);
            nw = Math.Min(nw, 80 - cw - 6);
            int gw = 80 - nw - cw - 6;
            for (int i = 0; i < stats.Count; i++)
            {
                var s = stats[i];
                var name = names[i];
                if (name.Length > nw) name = "..." + name[(name.Length - nw + 3)..];
                if (s.N is not { } n)
                {
                    write($" {name.PadRight(nw)} | {"Bin".PadLeft(cw)} {s.SA} -> {s.SB} bytes\n");
                    continue;
                }
                long plus = n.Ins, minus = n.Dels;
                if (maxChange > gw && gw > 0)
                {
                    plus = plus > 0 ? 1 + plus * (gw - 1) / maxChange : 0;
                    minus = minus > 0 ? 1 + minus * (gw - 1) / maxChange : 0;
                }
                var graph = new string('+', (int)plus) + new string('-', (int)minus);
                write($" {name.PadRight(nw)} | {counts[i].PadLeft(cw)}" + (graph.Length > 0 ? " " + graph : "") + "\n");
            }
        }
        var line = $" {files} file{(files != 1 ? "s" : "")} changed";
        if (ins > 0 || dels == 0) line += $", {ins} insertion{(ins != 1 ? "s" : "")}(+)";
        if (dels > 0 || ins == 0) line += $", {dels} deletion{(dels != 1 ? "s" : "")}(-)";
        write(line + "\n");
    }

    static void WritePatch(Cmd g, Repo repo, Action<string> write, string st, string? old, string? @new, (string Mode, string Sha)? x, (string Mode, string Sha)? y,
        int context, bool aWorktree, bool bWorktree)
    {
        string an = (old ?? @new)!, bn = (@new ?? old)!;
        write($"diff --git {CQuote("a/" + an)} {CQuote("b/" + bn)}\n");
        var xd = BlobData(repo, x, old ?? "", aWorktree);
        var yd = BlobData(repo, y, @new ?? "", bWorktree);
        var xs = x?.Sha ?? new string('0', 40);
        var ys = y?.Sha ?? new string('0', 40);
        if (st == "A") write($"new file mode {y!.Value.Mode}\n");
        else if (st == "D") write($"deleted file mode {x!.Value.Mode}\n");
        else if (x!.Value.Mode != y!.Value.Mode) write($"old mode {x.Value.Mode}\nnew mode {y.Value.Mode}\n");
        if (st[0] == 'R') write($"similarity index {Score(st)}%\nrename from {CQuote(old!)}\nrename to {CQuote(@new!)}\n");
        if (xs == ys) return;
        bool sameMode = x != null && y != null && x.Value.Mode == y.Value.Mode;
        write($"index {Abbrev(xs)}..{Abbrev(ys)}" + (sameMode ? $" {x!.Value.Mode}" : "") + "\n");
        var left = st == "A" ? "/dev/null" : CQuote("a/" + an);
        var right = st == "D" ? "/dev/null" : CQuote("b/" + bn);
        if (IsBinary(xd) || IsBinary(yd))
        {
            write(Msg("binaryDiffer", ("a", left), ("b", right)) + "\n");
            return;
        }
        write($"--- {left}\n+++ {right}\n");
        foreach (var line in Unified(SplitLines(xd), SplitLines(yd), context, g.Check)) write(line + "\n");
    }

    static List<string> SummaryLines(List<Change> changes, FileMap a, FileMap b)
    {
        var output = new List<string>();
        foreach (var c in changes)
        {
            if (c.St == "A") output.Add($" create mode {b[c.New!].Mode} {CQuote(c.New!)}");
            else if (c.St == "D") output.Add($" delete mode {a[c.Old!].Mode} {CQuote(c.Old!)}");
            else if (c.St[0] == 'R') output.Add($" rename {RenameLabel(c.Old!, c.New!)} ({Score(c.St)}%)");
            else if (a[c.Old!].Mode != b[c.New!].Mode && c.St == "M") output.Add($" mode change {a[c.Old!].Mode} => {b[c.New!].Mode} {CQuote(c.New!)}");
        }
        return output;
    }

    static List<Stat> StatEntries(Cmd g, Repo repo, FileMap a, FileMap b, List<Change> changes)
    {
        var stats = new List<Stat>();
        foreach (var c in changes)
        {
            var x = c.Old != null ? repo.Typed(a[c.Old].Sha, "blob") : [];
            var y = c.New != null ? repo.Typed(b[c.New].Sha, "blob") : [];
            if (IsBinary(x) || IsBinary(y)) stats.Add(new Stat(c.St, c.Old, c.New, null, x.Length, y.Length));
            else stats.Add(new Stat(c.St, c.Old, c.New, x.AsSpan().SequenceEqual(y) ? (0, 0) : CountChanges(x, y, g.Check), x.Length, y.Length));
        }
        return stats;
    }
}
