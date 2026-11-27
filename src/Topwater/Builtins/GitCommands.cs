using System.Text;
using System.Text.RegularExpressions;
using static Topwater.GitCore;
using FileMap = System.Collections.Generic.Dictionary<string, (string Mode, string Sha)>;

namespace Topwater;

internal static partial class Git
{
    static string Ljust(string s, int w) => s.PadRight(w);
    static string Z40 => new('0', 40);
    static IEnumerable<Entry> StageEntries(Dictionary<string, Dictionary<int, Entry>> unmerged) => unmerged.Values.SelectMany(st => st.Values);

    // ---------- init ----------

    static int GitInit(Cmd g, List<string> args)
    {
        var o = Parse("init", args);
        var ops = o.Operands;
        if (ops.Count > 1) throw UsageError("init", Msg("tooManyArguments"));
        var target = ops.Count > 0 ? VirtualFileSystem.Normalize(g.Cwd, ops[0]) : g.Cwd;
        var fs = g.Ctx.Fs;
        if (!fs.Exists(target)) fs.CreateDirectory(target, true);
        var gd = target.TrimEnd('/') + "/.git";
        if (fs.IsSymlink(gd)) throw Fatal("symlinkInGitDir", ("path", gd));
        var shown = gd + "/";
        if (fs.IsDirectory(gd, false) && fs.IsFile(gd + "/HEAD", false))
        {
            if (!o.Has("q")) g.Out(SubMsg("init", "reinitialized", ("path", shown)) + "\n");
            return 0;
        }
        var branch = o.Val("b") ?? g.Config("init.defaultbranch");
        foreach (var d in new[] { "objects/info", "objects/pack", "refs/heads", "refs/tags", "info", "hooks", "branches" }) fs.CreateDirectory(gd + "/" + d, true);
        if (string.IsNullOrEmpty(branch)) branch = "master";
        if (!Regex.IsMatch(branch, @"^[A-Za-z0-9._/-]+\z") || branch.Contains("..") || branch.EndsWith('.') || branch.EndsWith('/') || branch.EndsWith(".lock")
            || branch.StartsWith('-') || branch.StartsWith('/'))
            throw SubFatal("init", "badBranchName", ("name", branch));
        fs.WriteAllText(gd + "/HEAD", $"ref: refs/heads/{branch}\n");
        fs.WriteAllText(gd + "/config", "[core]\n\trepositoryformatversion = 0\n\tfilemode = true\n\tbare = false\n\tlogallrefupdates = true\n");
        fs.WriteAllText(gd + "/description", "Unnamed repository; edit this file 'description' to name the repository.\n");
        fs.WriteAllText(gd + "/info/exclude", "# git ls-files --others --exclude-from=.git/info/exclude\n# Lines that start with '#' are comments.\n");
        if (!o.Has("q")) g.Out(SubMsg("init", "initialized", ("path", shown)) + "\n");
        return 0;
    }

    // ---------- status ----------

    static int GitStatus(Cmd g, List<string> args)
    {
        var umode = "normal";
        var rest = new List<string>();
        foreach (var raw in args)
        {
            var a = raw is "--porcelain=v1" or "--porcelain=1" ? "--porcelain" : raw;
            var m = Regex.Match(a, @"^(?:-u|--untracked-files)(?:=?(no|normal|all))?\z");
            if (m.Success) umode = m.Groups[1].Success ? m.Groups[1].Value : "all";
            else rest.Add(a);
        }
        var o = Parse("status", rest);
        var repo = g.Repo();
        var specs = UserSpecs(repo, o.Operands);
        var s = new Status(repo, umode);
        if (specs != null)
        {
            s.Staged = s.Staged.Where(c => PathspecMatch(specs, c.New ?? c.Old!)).ToList();
            s.Unstaged = s.Unstaged.Where(c => PathspecMatch(specs, c.Old!)).ToList();
            s.Untracked = s.Untracked.Where(u => PathspecMatch(specs, u.TrimEnd('/')) || specs.Any(x => x.StartsWith(u, StringComparison.Ordinal))).ToList();
            s.Unmerged = s.Unmerged.Where(kv => PathspecMatch(specs, kv.Key)).ToDictionary(StringComparer.Ordinal);
        }
        bool porcelain = o.Has("\x01");
        if (o.Has("s") || porcelain)
        {
            if (o.Has("b")) g.Out("## " + BranchLine(repo) + "\n");
            Func<string, string> show = porcelain ? ShortQuote : p => ShortQuote(repo.Show(p));
            foreach (var line in ShortLines(s, show)) g.Out(line + "\n");
            return 0;
        }
        g.Out(LongStatus(repo, s));
        return 0;
    }

    static string ShortQuote(string p)
    {
        var q = CQuote(p);
        return q == p && p.Contains(' ') ? $"\"{p}\"" : q;
    }

    static string BranchLine(Repo repo)
    {
        var b = repo.Branch();
        if (b == null) return "HEAD (no branch)";
        if (repo.Head() == null) return SubMsg("status", "noCommitsOnBranch", ("branch", b));
        return TrackingBrief(repo, b) is { } t ? $"{b}...{t.Name}" + (t.Brief.Length > 0 ? $" [{t.Brief}]" : "") : b;
    }

    static readonly Dictionary<string, string> UnmergedCodes = new() { ["123"] = "UU", ["23"] = "AA", ["1"] = "DD", ["12"] = "UD", ["13"] = "DU", ["2"] = "AU", ["3"] = "UA" };
    static readonly Dictionary<string, string> UnmergedLabels = new()
    {
        ["UU"] = "bothModified", ["AA"] = "bothAdded", ["DD"] = "bothDeleted", ["UD"] = "deletedByThem", ["DU"] = "deletedByUs", ["AU"] = "addedByUs", ["UA"] = "addedByThem",
    };

    static string UnmergedCode(Dictionary<int, Entry> stages) => UnmergedCodes[string.Concat(stages.Keys.Order())];

    static List<string> ShortLines(Status s, Func<string, string> show)
    {
        var rows = new Dictionary<string, (string X, string Y, string? Old)>(StringComparer.Ordinal);
        foreach (var c in s.Staged) rows[c.New ?? c.Old!] = (c.St[..1], " ", c.St[0] == 'R' ? c.Old : null);
        foreach (var c in s.Unstaged) rows[c.Old!] = (rows.TryGetValue(c.Old!, out var r) ? r.X : " ", c.St, rows.TryGetValue(c.Old!, out var r2) ? r2.Old : null);
        foreach (var (p, stages) in s.Unmerged) rows[p] = (UnmergedCode(stages), "", null);
        var output = new List<string>();
        foreach (var p in rows.Keys.Order(ByBytes))
        {
            var (x, y, old) = rows[p];
            output.Add($"{x}{y} " + (old != null ? $"{show(old)} -> {show(p)}" : show(p)));
        }
        output.AddRange(s.Untracked.Select(u => $"?? {show(u)}"));
        return output;
    }

    static string LongStatus(Repo repo, Status s, bool committing = false)
    {
        string M(string key, params (string, string)[] v) => SubMsg("status", key, v);
        var output = new List<string>();
        var b = repo.Branch();
        if (b == null)
        {
            var (at, name) = DetachedName(repo);
            output.Add(M(at ? "detachedAt" : "detachedFrom", ("commit", name)));
        }
        else
        {
            output.Add(M("onBranch", ("branch", b)));
            if (s.Head != null && TrackingText(repo, b) is { } t) output.AddRange([t, ""]);
        }
        bool merging = repo.GExists("MERGE_HEAD");
        if (repo.GExists("rebase-apply/next")) output.AddRange([M("amInProgress"), ""]);
        if (repo.GExists("BISECT_START")) output.AddRange([M("bisecting", ("branch", (repo.GText("BISECT_START") ?? "").Trim())), ""]);
        if (s.Head == null) output.AddRange(["", committing ? M("initialCommit") : M("noCommitsYet"), ""]);
        if (merging)
        {
            output.Add(s.Unmerged.Count > 0 ? M("unmergedHeader") : M("mergeFixed"));
            output.Add("");
        }
        string Label(string key) => Ljust(M(key) + " ", 12);
        string Show(string p) => CQuote(repo.Show(p));
        if (s.Staged.Count > 0)
        {
            output.Add(M("toBeCommitted"));
            if (!merging) output.Add(s.Head == null ? M("hintUnstageNoCommits") : M("hintUnstage"));
            foreach (var c in s.Staged)
            {
                var key = c.St[..1] switch { "A" => "labelNew", "M" => "labelModified", "D" => "labelDeleted", "R" => "labelRenamed", _ => "labelTypechange" };
                output.Add("\t" + Label(key) + (c.St[0] == 'R' ? $"{Show(c.Old!)} -> {Show(c.New!)}" : Show(c.New ?? c.Old!)));
            }
            output.Add("");
        }
        if (s.Unmerged.Count > 0)
        {
            output.Add(M("unmergedPaths"));
            if (!merging) output.Add(M("hintUnstage"));
            output.Add(M("hintResolve"));
            foreach (var p in s.Unmerged.Keys.Order(GitCore.Ordinal))
                output.Add("\t" + Ljust(M(UnmergedLabels[UnmergedCode(s.Unmerged[p])]) + " ", 17) + Show(p));
            output.Add("");
        }
        if (s.Unstaged.Count > 0)
        {
            output.Add(M("notStaged"));
            output.Add(s.Unstaged.Any(c => c.St == "D") ? M("hintAddRm") : M("hintAdd"));
            output.Add(M("hintRestore"));
            foreach (var c in s.Unstaged)
                output.Add("\t" + Label(c.St switch { "M" => "labelModified", "D" => "labelDeleted", _ => "labelTypechange" }) + Show(c.Old!));
            output.Add("");
        }
        if (s.Untracked.Count > 0)
        {
            output.Add(M("untrackedHeader"));
            output.Add(M("hintTrack"));
            foreach (var u in s.Untracked) output.Add("\t" + Show(u));
            output.Add("");
        }
        if (s.Staged.Count == 0)
        {
            if (s.Unstaged.Count > 0 || s.Unmerged.Count > 0) output.Add(M("noChangesAdded"));
            else if (s.Untracked.Count > 0) output.Add(M("nothingAddedUntracked"));
            else if (s.Head == null) output.Add(M("nothingToCommitInitial"));
            else output.Add(M("nothingToCommitClean"));
        }
        return string.Join("\n", output) + "\n";
    }

    // ---------- add / rm / mv ----------

    static int GitAdd(Cmd g, List<string> args)
    {
        var o = Parse("add", args);
        var repo = g.Repo();
        var items = o.Operands;
        if (items.Count == 0 && !o.Has("A") && !o.Has("u"))
        {
            g.Err(SubMsg("add", "nothingSpecified") + "\n");
            return 0;
        }
        var specs = items.Count > 0 ? UserSpecs(repo, items)! : [""];
        var entries = repo.Index();
        var idx = repo.Stage0(entries);
        var unmerged = repo.Unmerged(entries);
        var tracked = idx.Keys.Union(unmerged.Keys).ToHashSet(StringComparer.Ordinal);
        // every spec must match something: a tracked path or a work tree file
        var ignoredHits = new List<string>();
        var raws = items.Count > 0 ? items : ["."];
        for (int k = 0; k < specs.Count; k++)
        {
            string spec = specs[k], raw = raws[k];
            bool hit = false;
            var full = repo.Wt(spec);
            if (tracked.Any(p => PathspecMatch([spec], p))) hit = true;
            if (spec.Length == 0 || repo.Fs.Exists(full, false)) hit = true;
            if (IsGlob(spec) && repo.Walk("").Any(p => PathspecMatch([spec], p.TrimEnd('/')))) hit = true;
            if (!hit) throw Fatal("pathspecNoMatch", ("path", raw));
            bool isDir = repo.Fs.IsDirectory(full, false);
            if (spec.Length > 0 && repo.Fs.Exists(full, false) && !tracked.Any(p => PathspecMatch([spec], p)) && repo.Ignored(spec, isDir))
                if (!o.Has("f")) ignoredHits.Add(spec);
        }
        if (ignoredHits.Count > 0)
        {
            g.Err(SubMsg("add", "ignoredPaths", ("paths", string.Join("\n", ignoredHits.Select(repo.Show)))) + "\n");
            return 1;
        }
        var changed = new List<(string What, string Path)>();
        // tracked paths: update or remove
        foreach (var p in tracked.Order(GitCore.Ordinal))
        {
            if (!PathspecMatch(specs, p)) continue;
            if (repo.WtStat(p) is not { } w)
            {
                idx.Remove(p);
                unmerged.Remove(p);
                changed.Add(("remove", p));
                continue;
            }
            var sha = ObjectId("blob", w.Data);
            if (unmerged.ContainsKey(p) || idx[p].Sha != sha || idx[p].Mode != w.Mode)
            {
                changed.Add(("add", p));
                if (!o.Has("n")) repo.Put("blob", w.Data);
                unmerged.Remove(p);
                idx[p] = repo.EntryFor(p, w.Mode, sha);
            }
        }
        if (!o.Has("u"))
        {
            foreach (var spec in specs)
            {
                var full = repo.Wt(spec);
                List<string> files;
                if (spec.Length > 0 && !repo.Fs.IsDirectory(full, false) && !IsGlob(spec))
                    files = repo.Fs.Exists(full, false) && repo.WtReachable(spec) ? [spec] : [];
                else
                {
                    var walkBase = spec.Length > 0 && repo.Fs.IsDirectory(full, false) ? spec : "";
                    files = repo.Walk(walkBase).Where(p => !p.EndsWith('/')).Where(p => PathspecMatch([spec], p) && !repo.Ignored(p)).ToList();
                    foreach (var p in repo.Walk(walkBase))
                        if (p.EndsWith('/') && PathspecMatch([spec], p.TrimEnd('/'))) throw SubFatal("add", "nestedRepo", ("path", p.TrimEnd('/')));
                }
                foreach (var p in files)
                {
                    if (idx.ContainsKey(p) || unmerged.ContainsKey(p)) continue;
                    CheckPath(p);
                    if (repo.WtStat(p) is not { } w) continue;
                    var sha = !o.Has("n") ? repo.Put("blob", w.Data) : ObjectId("blob", w.Data);
                    idx[p] = repo.EntryFor(p, w.Mode, sha);
                    changed.Add(("add", p));
                }
            }
        }
        if (o.Has("v") || o.Has("n"))
            foreach (var (what, p) in changed.Distinct().OrderBy(c => c.Path, GitCore.Ordinal)) g.Out($"{what} '{repo.Show(p)}'\n");
        if (!o.Has("n")) repo.SaveIndex(idx.Values.Concat(StageEntries(unmerged)));
        return 0;
    }

    static int GitRm(Cmd g, List<string> args)
    {
        var o = Parse("rm", args);
        var repo = g.Repo();
        var items = o.Operands;
        if (items.Count == 0) throw UsageError("rm", SubMsg("rm", "noPathspec"));
        var entries = repo.Index();
        var idx = repo.Stage0(entries);
        var unmerged = repo.Unmerged(entries);
        var tracked = idx.Keys.Union(unmerged.Keys).Order(GitCore.Ordinal).ToList();
        var head = TreeFiles(repo, repo.Head());
        var targets = new List<string>();
        foreach (var raw in items)
        {
            var spec = repo.FromUser(raw);
            var hits = tracked.Where(p => PathspecMatch([spec], p)).ToList();
            if (hits.Count == 0) throw Fatal("pathspecNoMatch", ("path", raw));
            if (!o.Has("r") && hits.Any(p => p != spec) && !IsGlob(spec)) throw SubFatal("rm", "notRecursive", ("path", raw));
            foreach (var p in hits) if (!targets.Contains(p)) targets.Add(p);
        }
        if (!o.Has("f"))
        {
            List<string> staged = [], local = [], both = [];
            foreach (var p in targets)
            {
                if (!idx.TryGetValue(p, out var e)) continue;
                var w = repo.WtId(p);
                var inHead = Get(head, p);
                bool stagedChange = inHead != e.Id;
                bool localChange = w != null && w != e.Id;
                if (o.Has("\x01"))  // --cached: only refuses when the file matches neither HEAD nor the work tree
                {
                    if (stagedChange && localChange && inHead != null) both.Add(p);
                    continue;
                }
                if (stagedChange && localChange) both.Add(p);
                else if (stagedChange) staged.Add(p);
                else if (localChange) local.Add(p);
            }
            foreach (var (one, many, paths) in new[] { ("stagedAndLocal", "stagedAndLocalMany", both), ("staged", "stagedMany", staged), ("local", "localMany", local) })
                if (paths.Count > 0)
                    throw new GitException(SubMsg("rm", paths.Count > 1 ? many : one, ("paths", string.Join("\n", paths.Select(p => "    " + repo.Show(p))))), 1);
        }
        foreach (var p in targets)
        {
            if (!o.Has("q")) g.Out($"rm '{p}'\n");
            if (o.Has("n")) continue;
            idx.Remove(p);
            unmerged.Remove(p);
            if (!o.Has("\x01")) repo.WtRemove(p);
        }
        if (!o.Has("n")) repo.SaveIndex(idx.Values.Concat(StageEntries(unmerged)));
        return 0;
    }

    static int GitMv(Cmd g, List<string> args)
    {
        var o = Parse("mv", args);
        var repo = g.Repo();
        var items = o.Operands;
        if (items.Count < 2) throw UsageError("mv", SubMsg("mv", "usage"));
        var entries = repo.Index();
        var idx = repo.Stage0(entries);
        var destRaw = items[^1];
        var dest = repo.FromUser(destRaw);
        bool destIsDir = repo.Fs.IsDirectory(repo.Wt(dest), false);
        if (items.Count > 2 && !destIsDir) throw SubFatal("mv", "destinationNotDir", ("path", destRaw));
        var moves = new List<(string Src, string Target, List<string> Under)>();
        foreach (var raw in items[..^1])
        {
            var src = repo.FromUser(raw);
            var target = destIsDir ? (dest.Length > 0 ? dest + "/" : "") + src[(src.LastIndexOf('/') + 1)..] : dest;
            var full = repo.Wt(src);
            if (!repo.Fs.Exists(full, false)) throw SubFatal("mv", "badSource", ("source", raw), ("destination", destRaw));
            var under = idx.Keys.Where(p => p.StartsWith(src + "/", StringComparison.Ordinal)).ToList();
            if (!idx.ContainsKey(src) && under.Count == 0) throw SubFatal("mv", "notUnderControl", ("source", raw), ("destination", destRaw));
            if (repo.Fs.Exists(repo.Wt(target), false) && !o.Has("f")) throw SubFatal("mv", "destinationExists", ("source", raw), ("destination", destRaw));
            moves.Add((src, target, under));
        }
        foreach (var (src, target, under) in moves)
        {
            if (o.Has("v") || o.Has("n")) g.Out($"Renaming {repo.Show(src)} to {repo.Show(target)}\n");
            if (o.Has("n")) continue;
            if (repo.Fs.Exists(repo.Wt(target), false)) repo.Fs.Delete(repo.Wt(target), recursive: true);
            var parent = target.Contains('/') ? target[..target.LastIndexOf('/')] : "";
            if (parent.Length > 0 && !repo.Fs.IsDirectory(repo.Wt(parent), false)) throw SubFatal("mv", "destinationNotDir", ("path", parent));
            repo.Fs.Move(repo.Wt(src), repo.Wt(target));
            foreach (var p in (idx.ContainsKey(src) ? new List<string> { src } : []).Concat(under))
            {
                if (!idx.Remove(p, out var e)) throw new KeyNotFoundException(p);
                var np = target + p[src.Length..];
                CheckPath(np);
                e.Path = np;
                idx[np] = e;
            }
        }
        if (!o.Has("n")) repo.SaveIndex(idx.Values.Concat(entries.Where(e => e.Stage != 0)));
        return 0;
    }

    // ---------- commit ----------

    static string CleanMessage(string text, bool stripComments = false)
    {
        var output = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (stripComments && line.StartsWith('#')) continue;
            if (line.Length == 0 && (output.Count == 0 || output[^1].Length == 0)) continue;
            output.Add(line);
        }
        while (output.Count > 0 && output[^1].Length == 0) output.RemoveAt(output.Count - 1);
        return output.Count > 0 ? string.Join("\n", output) + "\n" : "";
    }

    static string ReadMessageFile(Cmd g, string path)
    {
        if (path == "-") return g.Ctx.StdinText;
        var full = VirtualFileSystem.Normalize(g.Cwd, path);
        if (!g.Ctx.Fs.IsFile(full)) throw new GitException(SubMsg("commit", "cannotReadMessage", ("file", path)), 128);
        return Encoding.UTF8.GetString(g.Ctx.Fs.ReadAllBytes(full));
    }

    static int GitCommit(Cmd g, List<string> args)
    {
        var o = Parse("commit", args);
        var repo = g.Repo();
        var entries = repo.Index();
        var um = repo.Unmerged(entries);
        if (um.Count > 0)
        {
            g.Err(SubMsg("commit", "unmergedFiles") + "\n");
            g.Out(string.Concat(um.Keys.Order(GitCore.Ordinal).Select(p => $"U\t{repo.Show(p)}\n")));
            return 128;
        }
        var head = repo.Head();
        bool amend = o.Has("\x01");
        if (amend && head == null) throw SubFatal("commit", "nothingToAmend");
        if (amend && repo.GExists("MERGE_HEAD")) throw SubFatal("commit", "amendDuringMerge");
        var idx = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var e in entries) idx[e.Path] = e;
        var paths = o.Operands;
        if (paths.Count > 0 && o.Has("a")) throw SubFatal("commit", "pathsWithAll");
        if (o.Has("a"))
            foreach (var (p, e) in idx.ToList())
            {
                if (repo.WtStat(p) is not { } w) idx.Remove(p);
                else if (ObjectId("blob", w.Data) != e.Sha || w.Mode != e.Mode) idx[p] = repo.EntryFor(p, w.Mode, repo.Put("blob", w.Data));
            }
        var files = new FileMap(StringComparer.Ordinal);
        foreach (var (p, e) in idx) files[p] = (e.Mode, e.Sha);
        if (paths.Count > 0)
        {
            var specs = UserSpecs(repo, paths)!;
            var @base = TreeFiles(repo, head);
            var tracked = idx.Keys.Union(@base.Keys).ToHashSet(StringComparer.Ordinal);
            for (int k = 0; k < paths.Count; k++)
                if (!tracked.Any(p => PathspecMatch([specs[k]], p))) throw new GitException(SubMsg("commit", "pathspecUnknown", ("path", paths[k])), 1);
            files = new FileMap(@base, StringComparer.Ordinal);
            foreach (var p in tracked.Order(GitCore.Ordinal))
                if (PathspecMatch(specs, p))
                {
                    if (repo.WtStat(p) is not { } w)
                    {
                        files.Remove(p);
                        idx.Remove(p);
                    }
                    else
                    {
                        var sha = repo.Put("blob", w.Data);
                        files[p] = (w.Mode, sha);
                        idx[p] = repo.EntryFor(p, w.Mode, sha);
                    }
                }
        }
        // the message
        var mergeHeads = (repo.GText("MERGE_HEAD") ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(x => Sha.IsMatch(x)).ToList();
        var old = amend ? repo.Commit(head!) : null;
        string message;
        if (o.Vals("m").Count > 0) message = string.Join("\n\n", o.Vals("m"));
        else if (o.Val("F") is { } f) message = ReadMessageFile(g, f);
        else if (o.Has("\x04") && old != null) message = old.Message;
        else if (o.Has("\x04") && mergeHeads.Count > 0) message = repo.GText("MERGE_MSG") ?? "";
        else message = g.EditorError(old != null ? old.Message : mergeHeads.Count > 0 ? repo.GText("MERGE_MSG") ?? "" : null);
        message = CleanMessage(message, stripComments: o.Val("F") == null && o.Vals("m").Count == 0);
        if (message.Length == 0 && !o.Has("\x03")) throw new GitException(SubMsg("commit", "emptyMessage"), 1);
        var tree = repo.WriteTree(files);
        var parents = old != null ? [.. old.Parents] : (head != null ? new List<string> { head } : []).Concat(mergeHeads).ToList();
        if (!amend && mergeHeads.Count == 0 && !o.Has("\x02"))
        {
            var before = head != null ? repo.Commit(head).Tree : EmptyTree;
            if (tree == before)
            {
                repo.SaveIndex(idx.Values);
                g.Out(LongStatus(repo, new Status(repo), committing: true));
                return 1;
            }
        }
        // identities
        string? aName = null, aEmail = null;
        if (o.Val("\x05") is { } given)
        {
            var m = Regex.Match(given, @"^\s*(.*?)\s*<([^<>]*)>\s*\z");
            if (!m.Success) throw SubFatal("commit", "badAuthor", ("author", given));
            (aName, aEmail) = (m.Groups[1].Value, m.Groups[2].Value);
        }
        string author;
        if (old != null && aName == null && o.Val("\x06") == null) author = old.Author;
        else
        {
            if (old != null && aName == null)
            {
                var id = ParseIdent(old.Author);
                (aName, aEmail) = (id.Name, id.Email);
            }
            author = g.Ident("author", name: aName, email: aEmail, date: o.Val("\x06"));
        }
        var committer = g.Ident("committer");
        var body = $"tree {tree}\n" + string.Concat(parents.Select(p => $"parent {p}\n")) + $"author {author}\ncommitter {committer}\n\n{message}";
        var sha2 = repo.Put("commit", body);
        var c = repo.Commit(sha2);
        var kind = amend ? "commit (amend)" : mergeHeads.Count > 0 ? "commit (merge)" : parents.Count > 0 ? "commit" : "commit (initial)";
        repo.UpdateRef("HEAD", sha2, $"{kind}: {c.Subject}");
        if (repo.HeadRef() == null) repo.GWrite("HEAD", sha2 + "\n");
        repo.SaveIndex(idx.Values);
        foreach (var fn in new[] { "MERGE_HEAD", "MERGE_MSG", "MERGE_MODE" }) repo.GRemove(fn);
        if (!o.Has("q")) g.Out(CommitSummary(g, repo, c, root: parents.Count == 0 && !amend, amend: amend));
        return 0;
    }

    static string CommitSummary(Cmd g, Repo repo, Commit c, bool root, bool amend = false)
    {
        var b = repo.Branch();
        var where = b ?? "detached HEAD";
        var output = new List<string> { $"[{where}{(root ? " (root-commit)" : "")} {Abbrev(c.Sha)}] {c.Subject}" };
        var a = ParseIdent(c.Author);
        var cm = ParseIdent(c.Committer);
        if ((a.Name, a.Email) != (cm.Name, cm.Email)) output.Add($" Author: {a.Name} <{a.Email}>");
        if (amend || (a.Epoch, a.Zone) != (cm.Epoch, cm.Zone)) output.Add($" Date: {FormatDate(a.Epoch, a.Zone, "default", g.Now)}");
        var before = c.Parents.Count > 0 ? TreeFiles(repo, c.Parents[0]) : new FileMap(StringComparer.Ordinal);
        var after = repo.Flatten(c.Tree);
        var changes = DiffFiles(before, after, load: TreeLoader(repo, before, after));
        var text = new StringBuilder();
        if (changes.Count > 0) WriteStat(x => text.Append(x), StatEntries(g, repo, before, after, changes), shortStat: true);
        return string.Join("\n", output) + "\n" + text + string.Concat(SummaryLines(changes, before, after).Select(l => l + "\n"));
    }

    // ---------- log / show ----------

    static Dictionary<string, List<string>> Decorations(Repo repo)
    {
        var output = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var headRef = repo.HeadRef();
        var refs = repo.Refs("refs/heads/").Concat(repo.Refs("refs/tags/"))
            .Concat(RemoteBranches(repo).Where(r => r.Sha != null).Select(r => ("refs/remotes/" + r.Name, r.Sha!))).OrderByDescending(kv => kv.Item1, ByBytes);
        foreach (var (name, sha) in refs)
        {
            if (name == headRef) continue;
            var target = name.StartsWith("refs/tags/") ? Peel(repo, sha, "commit") : sha;
            var label = name.StartsWith("refs/tags/") ? "tag: " + name[10..] : name.StartsWith("refs/remotes/") ? name[13..] : name[11..];
            var key = target ?? sha;
            if (!output.TryGetValue(key, out var l)) output[key] = l = [];
            l.Add(label);
        }
        if (repo.Head() is { } head)
        {
            var b = repo.Branch();
            if (!output.TryGetValue(head, out var l)) output[head] = l = [];
            l.Insert(0, b != null ? $"HEAD -> {b}" : "HEAD");
        }
        return output;
    }

    static string ExpandFormat(Cmd g, Repo repo, string fmt, Commit c, Dictionary<string, List<string>>? decor, string dateStyle)
    {
        var a = ParseIdent(c.Author);
        var cm = ParseIdent(c.Committer);
        if (decor == null && (fmt.Contains("%d") || fmt.Contains("%D")))
        {
            decor = g.Decor ?? Decorations(repo);
            g.Decor = decor;
        }
        var d = decor != null && decor.TryGetValue(c.Sha, out var dl) ? dl : [];
        string Date(long epoch, string zone, string style) => FormatDate(epoch, zone, style, g.Now);
        var body = c.Body;
        var simple = new Dictionary<string, string>
        {
            ["H"] = c.Sha, ["h"] = Abbrev(c.Sha), ["T"] = c.Tree, ["t"] = Abbrev(c.Tree), ["P"] = string.Join(" ", c.Parents), ["p"] = string.Join(" ", c.Parents.Select(Abbrev)),
            ["an"] = a.Name, ["ae"] = a.Email, ["al"] = a.Email.Split('@')[0], ["ad"] = Date(a.Epoch, a.Zone, dateStyle), ["aD"] = Date(a.Epoch, a.Zone, "rfc"),
            ["ar"] = Date(a.Epoch, a.Zone, "relative"), ["at"] = a.Epoch.ToString(), ["ai"] = Date(a.Epoch, a.Zone, "iso"), ["aI"] = Date(a.Epoch, a.Zone, "iso-strict"),
            ["as"] = Date(a.Epoch, a.Zone, "short"),
            ["cn"] = cm.Name, ["ce"] = cm.Email, ["cl"] = cm.Email.Split('@')[0], ["cd"] = Date(cm.Epoch, cm.Zone, dateStyle), ["cD"] = Date(cm.Epoch, cm.Zone, "rfc"),
            ["cr"] = Date(cm.Epoch, cm.Zone, "relative"), ["ct"] = cm.Epoch.ToString(), ["ci"] = Date(cm.Epoch, cm.Zone, "iso"), ["cI"] = Date(cm.Epoch, cm.Zone, "iso-strict"),
            ["cs"] = Date(cm.Epoch, cm.Zone, "short"),
            ["s"] = c.Subject, ["b"] = body.EndsWith('\n') || body.Length == 0 ? body : body + "\n", ["B"] = c.Message,
            ["d"] = d.Count > 0 ? $" ({string.Join(", ", d)})" : "", ["D"] = string.Join(", ", d), ["n"] = "\n", ["%"] = "%",
            ["Cred"] = "", ["Cgreen"] = "", ["Cblue"] = "", ["Creset"] = "",
        };
        var keys = simple.Keys.OrderByDescending(k => k.Length).ToList();
        var sb = new StringBuilder();
        int i = 0;
        while (i < fmt.Length)
        {
            char ch = fmt[i];
            if (ch != '%') { sb.Append(ch); i++; continue; }
            if (string.CompareOrdinal(fmt, i, "%C(", 0, 3) == 0)
            {
                int j = fmt.IndexOf(')', i);
                if (j > 0) { i = j + 1; continue; }
            }
            if (i + 3 < fmt.Length + 0 && fmt[i + 1] == 'x' && Uri.IsHexDigit(fmt[i + 2]) && Uri.IsHexDigit(fmt[i + 3]))
            {
                sb.Append((char)Convert.ToInt32(fmt.Substring(i + 2, 2), 16));
                i += 4;
                continue;
            }
            var hit = keys.FirstOrDefault(k => string.CompareOrdinal(fmt, i + 1, k, 0, k.Length) == 0 && i + 1 + k.Length <= fmt.Length);
            if (hit != null)
            {
                sb.Append(simple[hit]);
                i += 1 + hit.Length;
            }
            else { sb.Append('%'); i++; }
        }
        return sb.ToString();
    }

    static readonly string[] Pretty = ["oneline", "short", "medium", "full", "fuller", "raw"];

    /// <summary>The commit as log prints it, ending in a newline (except a separator-style format). parents: --parents.</summary>
    static string CommitHeader(Cmd g, Repo repo, Commit c, string pretty, string? fmt, Dictionary<string, List<string>>? decor, string dateStyle, bool abbrevCommit,
        bool parents = false)
    {
        if (pretty is "format" or "tformat") return ExpandFormat(g, repo, fmt ?? "", c, decor, dateStyle) + (pretty == "tformat" ? "\n" : "");
        var d = decor != null && decor.TryGetValue(c.Sha, out var dl) ? dl : [];
        var dtext = d.Count > 0 ? $" ({string.Join(", ", d)})" : "";
        var sha = string.Join(" ", new[] { c.Sha }.Concat(parents ? c.Parents : []).Select(x => abbrevCommit ? Abbrev(x) : x));
        if (pretty == "oneline") return $"{sha}{dtext} {c.Subject}\n";
        var a = ParseIdent(c.Author);
        var cm = ParseIdent(c.Committer);
        var lines = new List<string> { $"commit {sha}{dtext}" };
        if (c.Parents.Count > 1) lines.Add("Merge: " + string.Join(" ", c.Parents.Select(Abbrev)));
        if (pretty == "raw")
        {
            lines = [$"commit {sha}", $"tree {c.Tree}", .. c.Parents.Select(p => $"parent {p}"), $"author {c.Author}", $"committer {c.Committer}"];
        }
        else if (pretty == "fuller")
            lines.AddRange([$"Author:     {a.Name} <{a.Email}>", $"AuthorDate: {FormatDate(a.Epoch, a.Zone, dateStyle, g.Now)}",
                $"Commit:     {cm.Name} <{cm.Email}>", $"CommitDate: {FormatDate(cm.Epoch, cm.Zone, dateStyle, g.Now)}"]);
        else
        {
            lines.Add($"Author: {a.Name} <{a.Email}>");
            if (pretty == "medium") lines.Add($"Date:   {FormatDate(a.Epoch, a.Zone, dateStyle, g.Now)}");
            if (pretty == "full") lines.Add($"Commit: {cm.Name} <{cm.Email}>");
        }
        lines.Add("");
        var text = c.Message.TrimEnd('\n');
        var msgLines = pretty == "short" ? [c.Subject] : (text.Length > 0 ? text.Split('\n') : []);
        lines.AddRange(msgLines.Select(l => l.Length > 0 ? "    " + l : "    "));
        return string.Join("\n", lines) + "\n";
    }

    static (string Pretty, string? Fmt) PrettyChoice(Opts o)
    {
        if (o.Has("\x0e")) return ("oneline", null);
        var v = o.Val("\x10");
        if (v == null) return ("medium", null);
        if (Pretty.Contains(v)) return (v, null);
        if (v.StartsWith("format:")) return ("format", v[7..]);
        if (v.StartsWith("tformat:")) return ("tformat", v[8..]);
        if (v.Contains('%') || v.Length == 0) return ("tformat", v);
        throw new GitException(Msg("badPretty", ("format", v)), 128);
    }

    static readonly string[] DiffKeys = ["\x01", "\x02", "\x03", "\x04", "\x06", "\x1f"];

    static bool DiffWanted(Opts o, bool defaultPatch)
    {
        if (o.Has("s")) return false;
        return defaultPatch || o.Has("p") || o.Has("u") || DiffKeys.Any(o.Has);
    }

    /// <summary>The commit's changes against its first parent, as text, in the format the options ask for (patch by default).
    /// A merge shows nothing, except to show, which lists its stat or names against the first parent (never a patch).</summary>
    static string CommitDiff(Cmd g, Repo repo, Commit c, Opts o, IReadOnlyList<string>? specs, bool merges = false)
    {
        if (c.Parents.Count > 1 && !(merges && DiffKeys.Any(o.Has))) return "";
        var a = c.Parents.Count > 0 ? TreeFiles(repo, c.Parents[0]) : new FileMap(StringComparer.Ordinal);
        var b = repo.Flatten(c.Tree);
        var buf = new StringBuilder();
        WriteDiff(g, repo, a, b, o, specs, output: x => buf.Append(x));
        return buf.ToString();
    }

    /// <summary>Splits operands into revisions (with ^x / a..b exclusions) and paths, the way log and diff tell them apart.</summary>
    static (List<string> Include, List<string> Exclude, List<string> Paths) RevisionsAndPaths(Repo repo, Opts o)
    {
        List<string> include = [], exclude = [], paths = [.. o.Paths];
        for (int k = 0; k < o.Args.Count; k++)
        {
            var a = o.Args[k];
            if (a.StartsWith('^') && a.Length > 1) { exclude.Add(CommitOf(repo, a[1..])); continue; }
            if (a.Contains("..."))
            {
                int at3 = a.IndexOf("...", StringComparison.Ordinal);
                string l3 = a[..at3], r3 = a[(at3 + 3)..];
                string lc = CommitOf(repo, l3.Length > 0 ? l3 : "HEAD"), rc = CommitOf(repo, r3.Length > 0 ? r3 : "HEAD");
                include.AddRange([lc, rc]);
                var common = Ancestors(repo, [lc]);
                common.IntersectWith(Ancestors(repo, [rc]));
                exclude.AddRange(common.Order(GitCore.Ordinal));
                continue;
            }
            if (a.Contains("..") && !a.Contains("..."))
            {
                int at = a.IndexOf("..", StringComparison.Ordinal);
                string left = a[..at], right = a[(at + 2)..];
                exclude.Add(CommitOf(repo, left.Length > 0 ? left : "HEAD"));
                include.Add(CommitOf(repo, right.Length > 0 ? right : "HEAD"));
                continue;
            }
            var sha = TryResolve(repo, a);
            if (sha != null)
            {
                include.Add(Peel(repo, sha, "commit") ?? throw Fatal("notCommit", ("rev", a)));
                continue;
            }
            if (repo.Fs.Exists(VirtualFileSystem.Normalize(repo.G.Cwd, a), false) && !o.DashDash)
            {
                paths.AddRange(o.Args[k..]);
                break;
            }
            throw Fatal("badRevision", ("rev", a));
        }
        return (include, exclude, paths);
    }

    static List<string> NumericShorthand(List<string> args) => args.Select(a => Regex.IsMatch(a, @"^-[0-9]+\z") ? "-n" + a[1..] : a).ToList();

    static List<string> AllRefCommits(Repo repo)
    {
        var output = new List<string>();
        foreach (var (_, s) in repo.Refs("refs/"))
            if (Peel(repo, s, "commit") is { } c) output.Add(c);
        if (repo.Head() is { } h) output.Add(h);
        return output;
    }

        static Regex UserRegex(string pattern)
    {
        try { return new Regex(pattern, RegexOptions.None, Shell.RegexTimeout); }
        catch (ArgumentException) { throw Fatal("badRegex", ("pattern", pattern)); }
    }

    static int GitLog(Cmd g, List<string> args)
    {
        var o = Parse("log", NumericShorthand(args));
        var repo = g.Repo();
        var (include, exclude, paths) = RevisionsAndPaths(repo, o);
        if (o.Has("\x12")) include.AddRange(AllRefCommits(repo));
        if (include.Count == 0 && !o.Has("\x12"))
        {
            var h = repo.Head();
            if (h == null) throw SubFatal("log", "noCommitsYet", ("branch", repo.Branch() ?? "HEAD"));
            include = [h];
        }
        var specs = UserSpecs(repo, paths);
        var nv = o.Val("n");
        long limit = nv != null && Regex.IsMatch(nv, @"^-?[0-9]+\z") ? (long.TryParse(nv, out var lv) ? lv : long.MaxValue) : -1;
        var sv = o.Val("\x1b", "0") ?? "";
        long skip = Regex.IsMatch(sv, @"^[0-9]+\z") ? (long.TryParse(sv, out var skv) ? skv : long.MaxValue) : 0;
        var (pretty, fmt) = PrettyChoice(o);
        var dateStyle = o.Val("\x19", "default")!;
        FormatDate(0, "+0000", dateStyle, g.Now);  // rejects an unknown style before any output
        var decor = o.Has("\x11") ? Decorations(repo) : null;
        bool abbrevCommit = o.Has("\x1a") || o.Has("\x0e");
        var excluded = exclude.Count > 0 ? Ancestors(repo, exclude) : [];
        List<string> order;
        if (o.Has("\x16"))
        {
            order = [];
            var c = include.Count > 0 ? include[0] : null;
            while (c != null && !excluded.Contains(c) && order.Count <= MaxWalk)
            {
                order.Add(c);
                var ps = repo.Commit(c).Parents;
                c = ps.Count > 0 ? ps[0] : null;
            }
        }
        else order = WalkOrder(repo, include.Distinct()).Where(c => !excluded.Contains(c)).ToList();
        long? since = null, until = null;
        foreach (var (key, name) in new[] { ("\x1d", "since"), ("\x1e", "until") })
            if (o.Val(key) is { } v)
            {
                var when = (ParseDate(v, g.Now) ?? throw new GitException(Msg("badDate", ("date", v)), 128)).Epoch;
                if (name == "since") since = when; else until = when;
            }
        var authorRx = o.Val("\x14") is { } ap ? UserRegex(ap) : null;
        var grepRx = o.Val("\x15") is { } gp ? UserRegex(gp) : null;
        var pickaxe = o.Val("S");
        Regex? pickaxeRx = null;
        if (o.Val("G") is { } gv)
        {
            if (Builtins.PosixRegexError(gv, true, grep: false) is { } error) throw new GitException(SubMsg("log", "badPickaxeRegex", ("error", error)), 128);
            pickaxeRx = new Regex(Builtins.PosixToNet(gv, true), RegexOptions.Multiline | RegexOptions.CultureInvariant, Shell.RegexTimeout);
        }
        var shown = new List<Commit>();
        var hits = new Dictionary<string, List<string>>(StringComparer.Ordinal);  // commit -> the paths the pickaxe found, which its diff is limited to
        foreach (var sha in order)
        {
            var c = repo.Commit(sha);
            if (o.Has("\x17") && c.Parents.Count > 1 || o.Has("\x18") && c.Parents.Count < 2) continue;
            if (since != null && CommitTime(repo, sha) < since || until != null && CommitTime(repo, sha) > until) continue;
            if (authorRx != null)
            {
                int gt = c.Author.LastIndexOf('>');
                if (!authorRx.IsMatch((gt >= 0 ? c.Author[..gt] : c.Author) + ">")) continue;
            }
            if (grepRx != null && !grepRx.IsMatch(c.Message)) continue;
            if (specs != null)
            {
                var a = c.Parents.Count > 0 ? TreeFiles(repo, c.Parents[0]) : new FileMap(StringComparer.Ordinal);
                if (DiffFiles(a, repo.Flatten(c.Tree), specs).Count == 0) continue;
            }
            if (pickaxe != null || pickaxeRx != null)
            {
                if (c.Parents.Count > 1) continue;  // a merge has no diff of its own to search
                var found = PickaxePaths(g, repo, c, specs, pickaxe, pickaxeRx);
                if (found.Count == 0) continue;
                hits[sha] = found;
            }
            if (skip > 0) { skip--; continue; }
            if (limit >= 0 && shown.Count >= limit) break;
            shown.Add(c);
        }
        if (o.Has("\x13")) shown.Reverse();
        WriteLog(g, repo, shown, o, pretty, fmt, decor, dateStyle, abbrevCommit, specs, defaultPatch: false, graph: o.Has("\x0f"), hits: hits);
        return 0;
    }

    /// <summary>The files whose change in c alters the count of needle (-S), or adds or removes a line rx matches (-G).</summary>
    static List<string> PickaxePaths(Cmd g, Repo repo, Commit c, IReadOnlyList<string>? specs, string? needle, Regex? rx)
    {
        var a = c.Parents.Count > 0 ? TreeFiles(repo, c.Parents[0]) : new FileMap(StringComparer.Ordinal);
        var b = repo.Flatten(c.Tree);
        var output = new List<string>();
        void Hit(Change ch)
        {
            foreach (var p in new[] { ch.Old, ch.New })
                if (p != null && !output.Contains(p)) output.Add(p);
        }
        foreach (var ch in DiffFiles(a, b, specs))
        {
            g.Check();
            var x = ch.Old != null ? repo.Typed(a[ch.Old].Sha, "blob") : [];
            var y = ch.New != null ? repo.Typed(b[ch.New].Sha, "blob") : [];
            if (IsBinary(x) || IsBinary(y)) continue;
            if (needle != null)
            {
                var n = ByteText.Encode(needle);
                if (n.Length > 0 && Count(x, n) != Count(y, n)) Hit(ch);
                continue;
            }
            var (lx, ly) = (SplitLines(x), SplitLines(y));
            var changed = Myers(lx, ly, g.Check).SelectMany(r => lx.GetRange(r.S1, r.E1 - r.S1).Concat(ly.GetRange(r.S2, r.E2 - r.S2)));
            if (changed.Any(l => rx!.IsMatch(GitCore.Text(l).TrimEnd('\n')))) Hit(ch);
        }
        return output;
    }

    /// <summary>Non-overlapping occurrences of needle in data, as Python's bytes.count counts them.</summary>
    static int Count(byte[] data, byte[] needle)
    {
        int n = 0;
        for (int i = data.AsSpan().IndexOf(needle); i >= 0; )
        {
            n++;
            int next = data.AsSpan(i + needle.Length).IndexOf(needle);
            i = next < 0 ? -1 : i + needle.Length + next;
        }
        return n;
    }

    static void WriteLog(Cmd g, Repo repo, List<Commit> commits, Opts o, string pretty, string? fmt, Dictionary<string, List<string>>? decor, string dateStyle,
        bool abbrevCommit, IReadOnlyList<string>? specs, bool defaultPatch, bool graph = false, bool merges = false, Dictionary<string, List<string>>? hits = null)
    {
        if (graph)
            for (int i = 0; i < commits.Count; i++)
                if (commits[i].Parents.Count > 1 || (i + 1 < commits.Count && !(commits[i].Parents.Count == 1 && commits[i].Parents[0] == commits[i + 1].Sha)))
                    throw Fatal("graphNotLinear");  // a straight line would misdraw it
        bool wantsDiff = DiffWanted(o, defaultPatch);
        for (int i = 0; i < commits.Count; i++)
        {
            g.Check();
            var c = commits[i];
            // shortcut: with paths, --parents prints the real parents, not git's rewritten (simplified) ones
            var head = CommitHeader(g, repo, c, pretty, fmt, decor, dateStyle, abbrevCommit, parents: o.Has("\x05"));
            var diff = wantsDiff ? CommitDiff(g, repo, c, o, hits is { Count: > 0 } ? hits[c.Sha] : specs, merges) : "";
            bool multiLine = pretty is not ("oneline" or "format" or "tformat");
            var text = "";
            if (i > 0 && multiLine) text += "\n";
            if (pretty == "format")
            {
                text += (i > 0 ? "\n" : "") + head;
                if (diff.Length > 0) text += "\n" + diff;
            }
            else
            {
                text += head;
                if (diff.Length > 0) text += (pretty != "oneline" ? "\n" : "") + diff;
            }
            if (graph)
            {
                bool last = i == commits.Count - 1;
                var lines = text.Split('\n').ToList();
                string end;
                if (lines.Count > 0 && lines[^1] == "")
                {
                    lines.RemoveAt(lines.Count - 1);
                    end = "\n";
                }
                else end = "";
                if (i > 0 && multiLine && lines.Count > 0 && lines[0] == "") lines[0] = "| ";
                int start = i > 0 && multiLine ? 1 : 0;
                var output = lines.Take(start).ToList();
                for (int k = start; k < lines.Count; k++) output.Add((k == start ? "* " : last ? "  " : "| ") + lines[k]);
                text = string.Join("\n", output) + end;
            }
            g.Out(text);
        }
    }

    static int GitShow(Cmd g, List<string> args)
    {
        var o = Parse("show", args);
        var repo = g.Repo();
        var targets = o.Args.Count > 0 ? o.Args : ["HEAD"];
        var (pretty, fmt) = PrettyChoice(o);
        var dateStyle = o.Val("\x19", "default")!;
        var decor = o.Has("\x11") ? Decorations(repo) : null;
        bool first = true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in targets)
        {
            var sha = TryResolve(repo, t);
            if (sha == null)
            {
                if (t == "HEAD" && repo.Head() == null) throw Fatal("badDefaultRevision");
                MissingPath(repo, t);
                throw Fatal("badRevision", ("rev", t));
            }
            if (!seen.Add(sha)) continue;
            var (kind, data) = repo.Obj(sha);
            if (kind == "blob") { g.Out(ByteText.Decode(data)); continue; }
            if (kind == "tree")
            {
                g.Out($"tree {t}\n\n" + string.Concat(repo.Tree(sha).Select(e => e.Name + (e.Mode == "040000" ? "/" : "") + "\n")));
                continue;
            }
            if (kind == "tag")
            {
                var text = ByteText.Decode(data);
                int k = text.IndexOf("\n\n", StringComparison.Ordinal);
                string headPart = k >= 0 ? text[..k] : text, body = k >= 0 ? text[(k + 2)..] : "";
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var l in headPart.Split('\n'))
                    if (l.IndexOf(' ') is var sp and >= 0) fields[l[..sp]] = l[(sp + 1)..];
                var tg = ParseIdent(fields.GetValueOrDefault("tagger", ""));
                g.Out($"tag {fields.GetValueOrDefault("tag", "")}\nTagger: {tg.Name} <{tg.Email}>\nDate:   {FormatDate(tg.Epoch, tg.Zone, dateStyle, g.Now)}\n\n{body.TrimEnd('\n')}\n\n");
                sha = Peel(repo, sha, "commit");
                if (sha == null) continue;
            }
            var c = repo.Commit(sha);
            if (!first && pretty is not ("oneline" or "format" or "tformat")) g.Out("\n");
            first = false;
            WriteLog(g, repo, [c], o, pretty, fmt, decor, dateStyle, o.Has("\x1a") || o.Has("\x0e"), UserSpecs(repo, o.Paths), defaultPatch: true, merges: true);
        }
        return 0;
    }

    // ---------- diff ----------

    static int GitDiff(Cmd g, List<string> args)
    {
        var o = Parse("diff", args);
        var repo = g.Repo();
        bool cached = o.Has("\x08");
        var revs = new List<string>();
        var paths = new List<string>(o.Paths);
        for (int k = 0; k < o.Args.Count; k++)
        {
            var a = o.Args[k];
            if (a.Contains("..."))
            {
                int at = a.IndexOf("...", StringComparison.Ordinal);
                string left = a[..at], right = a[(at + 3)..];
                string lc = CommitOf(repo, left.Length > 0 ? left : "HEAD"), rc = CommitOf(repo, right.Length > 0 ? right : "HEAD");
                revs.AddRange([MergeBase(repo, lc, rc) ?? lc, rc]);
                continue;
            }
            if (a.Contains(".."))
            {
                int at = a.IndexOf("..", StringComparison.Ordinal);
                string left = a[..at], right = a[(at + 2)..];
                revs.AddRange([CommitOf(repo, left.Length > 0 ? left : "HEAD"), CommitOf(repo, right.Length > 0 ? right : "HEAD")]);
                continue;
            }
            if (TryResolve(repo, a) is { } sha) { revs.Add(sha); continue; }
            if (repo.Fs.Exists(VirtualFileSystem.Normalize(g.Cwd, a), false) && !o.DashDash)
            {
                paths.AddRange(o.Args[k..]);
                break;
            }
            throw Fatal("badRevision", ("rev", a));
        }
        var specs = UserSpecs(repo, paths);
        var entries = repo.Index();
        var idx = IndexFiles(entries);
        foreach (var (p, stages) in repo.Unmerged(entries))
            if (stages.TryGetValue(2, out var e2)) idx[p] = (e2.Mode, e2.Sha);
        FileMap TreeOf(string rev) => repo.Flatten(Peel(repo, rev, "tree") ?? throw Fatal("notTree", ("rev", rev)));
        if (revs.Count > 2) throw UsageError("diff", SubMsg("diff", "tooManyRevisions"));
        FileMap a2, b2;
        bool bw;
        if (revs.Count == 2) (a2, b2, bw) = (TreeOf(revs[0]), TreeOf(revs[1]), false);
        else if (cached)
        {
            var @base = revs.Count > 0 ? revs[0] : repo.Head();
            (a2, b2, bw) = (@base != null ? TreeOf(@base) : new FileMap(StringComparer.Ordinal), idx, false);
        }
        else if (revs.Count > 0)
        {
            a2 = TreeOf(revs[0]);
            (b2, bw) = (WorktreeFiles(repo, idx.Keys.Union(a2.Keys)), true);
        }
        else (a2, b2, bw) = (idx, WorktreeFiles(repo, idx.Keys), true);
        if (o.Has("\x10") && (o.Has("\x03") || o.Has("\x04"))) throw SubFatal("diff", "checkWith");
        if (o.Has("\x10") && !o.Has("\x05"))
        {
            var changes = DiffFiles(a2, b2, specs);
            bool bad = WhitespaceCheck(g, repo, a2, b2, changes, bw);
            return (bad ? 2 : 0) | (changes.Count > 0 && o.Has("\x07") ? 1 : 0);
        }
        bool changed = WriteDiff(g, repo, a2, b2, o, specs, bWorktree: bw);
        return changed && (o.Has("\x07") || o.Has("\x05")) ? 1 : 0;
    }

    /// <summary>diff --check: prints added lines with whitespace problems (git's default core.whitespace); true when any.</summary>
    static bool WhitespaceCheck(Cmd g, Repo repo, FileMap a, FileMap b, List<Change> changes, bool bWorktree)
    {
        static bool Blank(string line) => line.Trim(' ', '\t', '\r', '\n', '\v', '\f').Length == 0;
        static int TrailingBlanks(List<string> lines)
        {
            int n = 0;
            while (n < lines.Count && Blank(lines[^(1 + n)])) n++;
            return n;
        }
        bool bad = false;
        foreach (var ch in changes)
        {
            if (ch.New == null || ch.St == "T") continue;
            var x = BlobData(repo, Get(a, ch.Old), ch.Old ?? "", false);
            var y = BlobData(repo, b[ch.New], ch.New, bWorktree);
            if (IsBinary(x) || IsBinary(y)) continue;
            var (lx, ly) = (SplitLines(x), SplitLines(y));
            var name = CQuote(ch.New);
            foreach (var (_, _, s2, e2) in Myers(lx, ly, g.Check))
                for (int j = s2; j < e2; j++)
                {
                    var text = ly[j].TrimEnd('\n');
                    if (Regex.IsMatch(text, @"\A(<{7}|={7}|>{7})(?:[ \t\r]|\z)"))
                    {
                        bad = true;
                        g.Out(SubMsg("diff", "checkMarker", ("file", name), ("line", (j + 1).ToString())) + "\n");
                        continue;
                    }
                    var problems = new List<string>();
                    if (text.Length > 0 && text[^1] is ' ' or '\t' or '\r') problems.Add(SubMsg("diff", "checkTrailing"));
                    var indent = text[..(text.Length - text.TrimStart(' ', '\t').Length)];
                    if (indent.Length < text.Length && indent.Contains(" \t")) problems.Add(SubMsg("diff", "checkSpaceTab"));
                    if (problems.Count == 0) continue;
                    bad = true;
                    g.Out(SubMsg("diff", "checkLine", ("file", name), ("line", (j + 1).ToString()), ("problems", string.Join(", ", problems))) + "\n");
                    g.Out("+" + Text(text) + "\n");
                }
            int tail = TrailingBlanks(ly);
            if (tail > TrailingBlanks(lx))
            {
                bad = true;
                g.Out(SubMsg("diff", "checkBlankEof", ("file", name), ("line", (ly.Count - tail + 1).ToString())) + "\n");
            }
        }
        return bad;
    }
}
