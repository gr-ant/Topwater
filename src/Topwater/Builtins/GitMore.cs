using System.Text;
using System.Text.RegularExpressions;
using static Topwater.GitCore;
using FileMap = System.Collections.Generic.Dictionary<string, (string Mode, string Sha)>;

namespace Topwater;

internal static partial class Git
{
    // ---------- moving between commits ----------

    static bool ValidBranchName(string name)
    {
        if (name is "" or "HEAD" or "@" || name.StartsWith('-') || name.StartsWith('/') || name.StartsWith('.') || name.EndsWith('/') || name.EndsWith('.') || name.EndsWith(".lock"))
            return false;
        if (name.Contains("..") || name.Contains("//") || name.Contains("@{") || name.Contains("/.")) return false;
        return name.IndexOfAny([' ', '~', '^', ':', '?', '*', '[', '\\', '\x7f']) < 0 && name.All(c => c >= 32);
    }

    static string Tabbed(IEnumerable<string> paths) => string.Concat(paths.Select(p => $"\t{p}\n")).TrimEnd('\n');

    /// <summary>Moves the index and work tree from commit old to commit new, keeping local changes to paths the two do not
    /// differ in. Refuses (exit 1) when that would lose a local change or an untracked file. Returns the paths that
    /// stay locally changed.</summary>
    static List<string> SwitchTrees(Cmd g, Repo repo, string? old, string? @new, bool force, string verb = "checkout")
    {
        FileMap a = TreeFiles(repo, old), b = TreeFiles(repo, @new);
        var entries = repo.Index();
        var unmerged = repo.Unmerged(entries);
        if (unmerged.Count > 0 && !force)
            throw new GitException(SubMsg("checkout", "resolveIndexFirst", ("paths", string.Join("\n", unmerged.Keys.Order(GitCore.Ordinal).Select(p => Msg("needsMerge", ("path", p)))))), 1);
        var idx = IndexFiles(entries);
        if (force)
        {
            var idxEntries = new Dictionary<string, Entry>(StringComparer.Ordinal);
            foreach (var (p, t) in b)
            {
                if (repo.WtId(p) != t) repo.WtWrite(p, t);
                idxEntries[p] = repo.EntryFor(p, t);
            }
            foreach (var p in idx.Keys.Union(a.Keys).Union(unmerged.Keys))
                if (!b.ContainsKey(p)) repo.WtRemove(p);
            repo.SaveIndex(idxEntries.Values);
            return [];
        }
        List<string> plan = [], dirty = [], clobber = [];
        foreach (var p in a.Keys.Union(b.Keys).Order(GitCore.Ordinal))
        {
            var ap = Get(a, p);
            var bp = Get(b, p);
            if (ap == bp) continue;
            g.Check();
            var i = Get(idx, p);
            var w = repo.WtId(p);
            if (i == bp && w == i) continue;  // already as the target has it
            if (ap == null && i == null)
            {
                if (w != null && !repo.Ignored(p)) clobber.Add(p);
            }
            else if (i != ap || (i != null && w != i)) dirty.Add(p);
            plan.Add(p);
        }
        if (dirty.Count > 0)
            throw new GitException(SubMsg("checkout", "localChanges", ("verb", verb), ("paths", Tabbed(dirty)),
                ("advice", SubMsg("checkout", verb == "merge" ? "localChangesAdviceMerge" : "localChangesAdvice"))), 1);
        if (clobber.Count > 0)
            throw new GitException(SubMsg("checkout", "untrackedOverwritten", ("verb", verb), ("paths", Tabbed(clobber)),
                ("advice", SubMsg("checkout", verb == "merge" ? "untrackedAdviceMerge" : "untrackedAdvice"))), 1);
        var byPath = repo.Stage0(entries);
        foreach (var p in plan)
        {
            if (Get(b, p) is not { } t)
            {
                repo.WtRemove(p);
                byPath.Remove(p);
            }
            else
            {
                repo.WtWrite(p, t);
                byPath[p] = repo.EntryFor(p, t);
            }
        }
        repo.SaveIndex(byPath.Values);
        // what stays changed locally, against the new commit
        var output = new List<string>();
        var now = IndexFiles(byPath.Values);
        foreach (var p in now.Keys.Union(b.Keys).Order(GitCore.Ordinal))
            if (Get(now, p) != Get(b, p) || (now.ContainsKey(p) && repo.WtId(p) != now[p]))
                output.Add((!now.ContainsKey(p) || repo.WtId(p) == null ? "D" : !b.ContainsKey(p) ? "A" : "M") + "\t" + p);
        return output;
    }

    static string SubjectOf(Repo repo, string sha) => $"{Abbrev(sha)} {repo.Commit(sha).Subject}";

    static int MoveHead(Cmd g, Repo repo, string? targetRef, string? targetSha, string label, bool force, bool quiet,
        string? newBranch = null, bool resetBranch = false, bool stay = false)
    {
        var old = repo.Head();
        var oldBranch = repo.Branch();
        string M(string key, params (string, string)[] v) => SubMsg("checkout", key, v);
        if (targetRef != null && targetRef == repo.HeadRef() && newBranch == null && !stay)
        {
            if (!quiet) g.Err(M("alreadyOn", ("branch", label)) + "\n");
            return 0;
        }
        var carried = SwitchTrees(g, repo, old, targetSha, force);
        if (force)
            foreach (var f in new[] { "MERGE_HEAD", "MERGE_MSG", "MERGE_MODE" }) repo.GRemove(f);  // a forced switch gives up a merge in progress
        if (stay)
        {
            if (!quiet) foreach (var line in carried) g.Out(line + "\n");
            return 0;
        }
        if (newBranch != null && !string.IsNullOrEmpty(targetSha)) repo.UpdateRef("refs/heads/" + newBranch, targetSha, $"branch: Created from {label}");
        var fromName = oldBranch ?? old ?? "";
        if (targetRef != null) repo.SetHead(targetRef, $"checkout: moving from {fromName} to {newBranch ?? label}");
        else repo.SetHead(targetSha!, $"checkout: moving from {fromName} to {label}", detach: true);
        if (quiet) return 0;
        if (oldBranch == null && old != null && (targetRef != null || old != targetSha)) g.Err(M("previousPosition", ("commit", SubjectOf(repo, old))) + "\n");
        if (targetRef == null) g.Err(M("headNowAt", ("commit", SubjectOf(repo, targetSha!))) + "\n");
        else if (newBranch != null) g.Err(M(resetBranch ? "switchedResetBranch" : "switchedNewBranch", ("branch", newBranch)) + "\n");
        else g.Err(M("switchedBranch", ("branch", label)) + "\n");
        foreach (var line in carried) g.Out(line + "\n");
        return 0;
    }

    /// <summary>checkout [tree-ish] -- paths / restore: copies paths from a commit (or the index) into the index and/or work tree.</summary>
    static int CheckoutPaths(Cmd g, Repo repo, string? source, List<string> items, string sub, bool staged = false, bool worktree = true, bool quiet = false)
    {
        var entries = repo.Index();
        var idx = repo.Stage0(entries);
        var unmerged = repo.Unmerged(entries);
        var src = source != null ? TreeFiles(repo, source) : null;
        var specs = items.Select(repo.FromUser).ToList();
        var pool = src != null ? src.Keys.ToHashSet(StringComparer.Ordinal) : idx.Keys.Union(unmerged.Keys).ToHashSet(StringComparer.Ordinal);
        if (staged && src != null) pool.UnionWith(idx.Keys);
        int count = 0;
        for (int k = 0; k < items.Count; k++)
            if (!pool.Any(p => PathspecMatch([specs[k]], p))) throw new GitException(Msg("pathspecNoMatchKnown", ("path", items[k])), 1);
        foreach (var p in pool.Order(GitCore.Ordinal))
        {
            if (!PathspecMatch(specs, p)) continue;
            g.Check();
            (string Mode, string Sha)? t = src != null ? Get(src, p) : idx.TryGetValue(p, out var ie) ? ie.Id : null;
            if (t == null && src == null && unmerged.ContainsKey(p)) throw new GitException(SubMsg(sub, "unmergedPath", ("path", p)), 1);
            if (staged)
            {
                if (t is not { } tv) idx.Remove(p);
                else idx[p] = idx.TryGetValue(p, out var old) && old.Id == tv ? old : new Entry(p, tv.Sha, tv.Mode);
            }
            if (worktree)
            {
                if (t is not { } tv) repo.WtRemove(p);
                else
                {
                    if (repo.WtId(p) != tv) repo.WtWrite(p, tv);
                    if (src != null && !staged && sub == "checkout") idx[p] = repo.EntryFor(p, tv);
                }
                count++;
            }
        }
        repo.SaveIndex(idx.Values.Concat(StageEntries(unmerged)));
        if (sub == "checkout" && !quiet)
        {
            var key = source != null ? (count == 1 ? "updatedFromCommit" : "updatedFromCommitMany") : (count == 1 ? "updatedFromIndex" : "updatedFromIndexMany");
            g.Err(SubMsg("checkout", key, ("count", count.ToString()), ("commit", Abbrev(source ?? ""))) + "\n");
        }
        return 0;
    }

    static int GitCheckout(Cmd g, List<string> args)
    {
        var o = Parse("checkout", args);
        var repo = g.Repo();
        bool quiet = o.Has("q"), force = o.Has("f");
        var @new = o.Val("b") ?? o.Val("B");
        if (o.DashDash && @new == null)
        {
            if (o.Args.Count > 1) throw new GitException(Msg("pathspecNoMatchKnown", ("path", o.Args[1])), 1);
            var source = o.Args.Count > 0 ? CommitOf(repo, o.Args[0]) : null;
            if (o.Paths.Count == 0) return 0;
            return CheckoutPaths(g, repo, source, o.Paths, "checkout", staged: source != null, quiet: true);
        }
        var target = o.Args.Count > 0 ? o.Args[0] : null;
        if (target == "-") target = PreviousBranch(repo, 1) ?? throw SubFatal("checkout", "noPreviousBranch");
        if (@new != null)
        {
            if (!ValidBranchName(@new)) throw SubFatal("branch", "invalidName", ("name", @new));
            bool exists = repo.ReadRef("refs/heads/" + @new) != null;
            if (exists && o.Val("B") == null) throw SubFatal("branch", "exists", ("name", @new));
            var start = target != null ? CommitOf(repo, target) : repo.Head();
            if (start == null && target == null)
            {
                repo.SetHead("refs/heads/" + @new);
                if (!quiet) g.Err(SubMsg("checkout", "switchedNewBranch", ("branch", @new)) + "\n");
                return 0;
            }
            return MoveHead(g, repo, "refs/heads/" + @new, start, target ?? "HEAD", force, quiet, newBranch: @new, resetBranch: exists);
        }
        if (target == null) return o.Has("\x01") ? MoveHead(g, repo, null, repo.Head(), "HEAD", force, quiet) : 0;
        if (!o.Has("\x01") && target is "HEAD" or "@" && repo.HeadRef() is { } stayRef && o.Args.Count == 1)
            return MoveHead(g, repo, stayRef, repo.Head(), repo.Branch()!, force, quiet, stay: true);
        if (!o.Has("\x01") && repo.ReadRef("refs/heads/" + target) is { } bsha && o.Args.Count == 1)
            return MoveHead(g, repo, "refs/heads/" + target, bsha, target, force, quiet);
        var sha = TryResolve(repo, target);
        var c = sha != null ? Peel(repo, sha, "commit") : null;
        if (c != null)
        {
            if (o.Args.Count > 1) return CheckoutPaths(g, repo, c, o.Args[1..], "checkout", staged: true, quiet: quiet);
            return MoveHead(g, repo, null, c, target, force, quiet);
        }
        if (o.Has("\x01")) throw Fatal("badRevision", ("rev", target));
        return CheckoutPaths(g, repo, null, o.Args, "checkout", quiet: quiet);
    }

    static int GitSwitch(Cmd g, List<string> args)
    {
        var o = Parse("switch", args);
        var repo = g.Repo();
        bool quiet = o.Has("q"), force = o.Has("f");
        var @new = o.Val("c") ?? o.Val("C");
        var target = o.Args.Count > 0 ? o.Args[0] : null;
        if (o.Args.Count > 1) throw UsageError("switch", SubMsg("switch", "tooManyArguments"));
        if (target == "-") target = PreviousBranch(repo, 1) ?? throw SubFatal("checkout", "noPreviousBranch");
        if (@new != null)
        {
            if (!ValidBranchName(@new)) throw SubFatal("branch", "invalidName", ("name", @new));
            bool exists = repo.ReadRef("refs/heads/" + @new) != null;
            if (exists && o.Val("C") == null) throw SubFatal("branch", "exists", ("name", @new));
            var start = target != null ? CommitOf(repo, target) : repo.Head();
            if (start == null)
            {
                repo.SetHead("refs/heads/" + @new);
                if (!quiet) g.Err(SubMsg("checkout", "switchedNewBranch", ("branch", @new)) + "\n");
                return 0;
            }
            return MoveHead(g, repo, "refs/heads/" + @new, start, target ?? "HEAD", force, quiet, newBranch: @new, resetBranch: exists);
        }
        if (o.Has("d")) return MoveHead(g, repo, null, CommitOf(repo, target ?? "HEAD"), target ?? "HEAD", force, quiet);
        if (target == null) throw SubFatal("switch", "missingBranch");
        if (repo.ReadRef("refs/heads/" + target) is { } bsha) return MoveHead(g, repo, "refs/heads/" + target, bsha, target, force, quiet);
        if (target is "HEAD" or "@" && repo.HeadRef() is { } hr) throw SubFatal("switch", "expectedBranchRef", ("rev", hr));
        if (TryResolve(repo, target) != null) throw SubFatal("switch", "expectedBranch", ("rev", target));
        throw SubFatal("switch", "invalidReference", ("rev", target));
    }

    static int GitRestore(Cmd g, List<string> args)
    {
        var o = Parse("restore", args);
        var repo = g.Repo();
        var items = o.Operands;
        if (items.Count == 0) throw SubFatal("restore", "noPaths");
        bool staged = o.Has("S"), worktree = o.Has("W") || !o.Has("S");
        var src = o.Val("s");
        var source = !string.IsNullOrEmpty(src) ? CommitOf(repo, src) : (staged ? repo.Head() : null);
        if (staged && source == null && src == null)
        {
            // unborn branch: restoring the index means emptying it for those paths
            var specs = items.Select(repo.FromUser).ToList();
            repo.SaveIndex(repo.Index().Where(e => !PathspecMatch(specs, e.Path)).ToList());
            return 0;
        }
        return CheckoutPaths(g, repo, source, items, "restore", staged: staged, worktree: worktree);
    }

    static int GitReset(Cmd g, List<string> args)
    {
        var o = Parse("reset", args);
        var repo = g.Repo();
        var modes = new[] { ("soft", "\x01"), ("mixed", "\x02"), ("hard", "\x03") }.Where(m => o.Has(m.Item2)).Select(m => m.Item1).ToList();
        if (modes.Count > 1) throw SubFatal("reset", "conflictingModes");
        var mode = modes.Count > 0 ? modes[0] : "mixed";
        var rev = "HEAD";
        var paths = new List<string>(o.Paths);
        if (o.Args.Count > 0)
        {
            if (TryResolve(repo, o.Args[0]) != null)
            {
                rev = o.Args[0];
                paths = [.. o.Args[1..], .. paths];
            }
            else if (repo.Head() == null && o.Args[0] is "HEAD" or "@") paths = [.. o.Args[1..], .. paths];
            else if (!o.DashDash && o.Args.All(a => repo.Fs.Exists(VirtualFileSystem.Normalize(g.Cwd, a), false))) paths = [.. o.Args, .. paths];
            else throw Fatal("badRevision", ("rev", o.Args[0]));
        }
        var head = repo.Head();
        var target = rev is "HEAD" or "@" && head == null ? null : CommitOf(repo, rev);
        if (paths.Count > 0)
        {
            if (mode != "mixed") throw SubFatal("reset", "pathsWithMode", ("mode", mode));
            var specs = paths.Select(repo.FromUser).ToList();
            var src = TreeFiles(repo, target);
            var entries = repo.Index();
            var idx = repo.Stage0(entries);
            foreach (var p in src.Keys.Union(idx.Keys).Union(repo.Unmerged(entries).Keys).ToList())
                if (PathspecMatch(specs, p))
                {
                    if (Get(src, p) is not { } t) idx.Remove(p);
                    else if (!idx.TryGetValue(p, out var e) || e.Id != t) idx[p] = new Entry(p, t.Sha, t.Mode);
                }
            var keep = entries.Where(e => e.Stage != 0 && !PathspecMatch(specs, e.Path));
            repo.SaveIndex(idx.Values.Concat(keep).ToList());
            UnstagedReport(g, repo, o.Has("q"));
            return 0;
        }
        if (head != null) repo.GWrite("ORIG_HEAD", head + "\n");
        if (mode == "hard") SwitchTrees(g, repo, head, target, force: true);
        else if (mode == "mixed")
        {
            var src = TreeFiles(repo, target);
            var old = repo.Stage0();
            repo.SaveIndex(src.Select(kv => old.TryGetValue(kv.Key, out var e) && e.Id == kv.Value ? e : new Entry(kv.Key, kv.Value.Sha, kv.Value.Mode)).ToList());
        }
        if (target != null)
        {
            repo.UpdateRef("HEAD", target, $"reset: moving to {rev}");
            if (repo.HeadRef() == null) repo.GWrite("HEAD", target + "\n");
        }
        foreach (var f in new[] { "MERGE_HEAD", "MERGE_MSG", "MERGE_MODE" }) repo.GRemove(f);
        if (mode == "hard" && !o.Has("q") && target != null) g.Out(SubMsg("reset", "headNowAt", ("commit", SubjectOf(repo, target))) + "\n");
        if (mode == "mixed") UnstagedReport(g, repo, o.Has("q"));
        return 0;
    }

    static void UnstagedReport(Cmd g, Repo repo, bool quiet)
    {
        if (quiet) return;
        var idx = IndexFiles(repo.Index());
        var lines = new List<string>();
        foreach (var p in idx.Keys.Order(GitCore.Ordinal))
        {
            var w = repo.WtId(p);
            if (w == null) lines.Add($"D\t{repo.Show(p)}");
            else if (w != idx[p]) lines.Add($"M\t{repo.Show(p)}");
        }
        if (lines.Count > 0) g.Out(SubMsg("reset", "unstagedAfterReset") + "\n" + string.Join("\n", lines) + "\n");
    }

    // ---------- branch / tag ----------

    static Regex? NamePattern(string glob) => GlobFull(glob, false);

    static int GitBranch(Cmd g, List<string> args)
    {
        var o = Parse("branch", args);
        var repo = g.Repo();
        string M(string key, params (string, string)[] v) => SubMsg("branch", key, v);
        if (o.Has("\x01"))
        {
            if (repo.Branch() is { } cur) g.Out(cur + "\n");
            return 0;
        }
        if (o.Has("d") || o.Has("D"))
        {
            if (o.Args.Count == 0) throw SubFatal("branch", "nameRequired");
            int code = 0;
            var head = repo.Head();
            HashSet<string>? reachable = null;
            foreach (var name in o.Args)
            {
                var sha = repo.ReadRef("refs/heads/" + name);
                if (sha == null) { g.Err(M("notFound", ("name", name)) + "\n"); code = 1; continue; }
                if (repo.HeadRef() == "refs/heads/" + name) { g.Err(M("checkedOut", ("name", name), ("path", repo.Top)) + "\n"); code = 1; continue; }
                if (!(o.Has("D") || o.Has("f")) && (head == null || !(reachable ??= Ancestors(repo, [head])).Contains(sha)))
                {
                    g.Err(M("notMerged", ("name", name)) + "\n");
                    code = 1;
                    continue;
                }
                repo.DeleteRef("refs/heads/" + name);
                g.Out(M("deleted", ("name", name), ("commit", Abbrev(sha))) + "\n");
            }
            return code;
        }
        if (o.Has("m") || o.Has("M"))
        {
            var names = o.Args;
            string? old;
            string @new;
            if (names.Count == 1) (old, @new) = (repo.Branch(), names[0]);
            else if (names.Count == 2) (old, @new) = (names[0], names[1]);
            else throw SubFatal("branch", "renameArgs");
            if (old == null) throw SubFatal("branch", "renameDetached");
            if (!ValidBranchName(@new)) throw SubFatal("branch", "invalidName", ("name", @new));
            var sha = repo.ReadRef("refs/heads/" + old);
            if (sha == null && repo.HeadRef() != "refs/heads/" + old) throw SubFatal("branch", "noSuchBranch", ("name", old));
            if (repo.ReadRef("refs/heads/" + @new) != null && @new != old)
            {
                if (!o.Has("M")) throw SubFatal("branch", "exists", ("name", @new));
                if (repo.HeadRef() == "refs/heads/" + @new) throw SubFatal("branch", "forceCurrent", ("name", @new));
            }
            if (@new == old) return 0;
            bool current = repo.HeadRef() == "refs/heads/" + old;
            bool nested = @new.StartsWith(old + "/") || old.StartsWith(@new + "/");
            var log = sha != null ? repo.GRead("logs/refs/heads/" + old) : null;
            if (sha != null && nested) repo.DeleteRef("refs/heads/" + old);  // one name is a directory of the other: the old one has to go first
            // otherwise the new name and HEAD are written before the old name goes, so a failed write loses nothing
            if (sha != null)
            {
                repo.GWrite("refs/heads/" + @new, sha + "\n");
                if (log is { Length: > 0 }) repo.GWrite("logs/refs/heads/" + @new, log);
            }
            if (current) repo.GWrite("HEAD", $"ref: refs/heads/{@new}\n");
            if (sha != null && !nested) repo.DeleteRef("refs/heads/" + old);
            return 0;
        }
        bool listing = o.Has("\x02") || o.Has("a") || o.Has("r") || o.Has("v") || o.Args.Count == 0;
        if (listing)
        {
            if (o.Has("r") && !o.Has("a")) return 0;
            var pattern = o.Has("\x02") && o.Args.Count > 0 ? o.Args.Select(NamePattern).ToList() : null;
            var rows = new List<(string Mark, string Name, string Sha)>();
            var headRef = repo.HeadRef();
            if (headRef == null && repo.Head() is { } dh) rows.Add(("*", M("detachedLabel", ("commit", Abbrev(dh))), dh));
            foreach (var (name, sha) in repo.Refs("refs/heads/"))
            {
                var shortName = name[11..];
                if (pattern != null && !pattern.Any(p => p != null && p.IsMatch(shortName))) continue;
                rows.Add((name == headRef ? "*" : " ", shortName, sha));
            }
            int width = rows.Count > 0 ? rows.Max(r => r.Name.Length) : 0;
            foreach (var (mark, name, sha) in rows)
                g.Out(o.Has("v") ? $"{mark} {Ljust(name, width)} {Abbrev(sha)} {repo.Commit(sha).Subject}\n" : $"{mark} {name}\n");
            return 0;
        }
        // create
        var bname = o.Args[0];
        if (o.Args.Count > 2) throw SubFatal("branch", "tooManyArguments");
        if (!ValidBranchName(bname)) throw SubFatal("branch", "invalidName", ("name", bname));
        var existing = repo.ReadRef("refs/heads/" + bname);
        if (existing != null && !o.Has("f")) throw SubFatal("branch", "exists", ("name", bname));
        if (existing != null && o.Has("f") && repo.HeadRef() == "refs/heads/" + bname) throw SubFatal("branch", "forceCurrent", ("name", bname));
        var start = o.Args.Count > 1 ? o.Args[1] : "HEAD";
        var s2 = TryResolve(repo, start);
        s2 = s2 != null ? Peel(repo, s2, "commit") : null;
        if (s2 == null) throw SubFatal("branch", "notValidObject", ("name", start == "HEAD" ? repo.Branch() ?? "HEAD" : start));
        repo.UpdateRef("refs/heads/" + bname, s2, $"branch: Created from {start}");
        return 0;
    }

    static int GitTag(Cmd g, List<string> args)
    {
        var o = Parse("tag", args);
        var repo = g.Repo();
        string M(string key, params (string, string)[] v) => SubMsg("tag", key, v);
        if (o.Has("d"))
        {
            int code = 0;
            foreach (var name in o.Args)
            {
                var sha = repo.ReadRef("refs/tags/" + name);
                if (sha == null) { g.Err(M("notFound", ("name", name)) + "\n"); code = 1; continue; }
                repo.DeleteRef("refs/tags/" + name);
                g.Out(M("deleted", ("name", name), ("commit", Abbrev(sha))) + "\n");
            }
            return code;
        }
        if (o.Has("l") || o.Args.Count == 0)
        {
            var pats = o.Args.Select(NamePattern).ToList();
            foreach (var (name, _) in repo.Refs("refs/tags/"))
            {
                var shortName = name[10..];
                if (pats.Count == 0 || pats.Any(p => p != null && p.IsMatch(shortName))) g.Out(shortName + "\n");
            }
            return 0;
        }
        var tname = o.Args[0];
        if (o.Args.Count > 2) throw UsageError("tag", M("tooManyArguments"));
        if (!ValidBranchName(tname)) throw SubFatal("tag", "invalidName", ("name", tname));
        var old = repo.ReadRef("refs/tags/" + tname);
        if (old != null && !o.Has("f")) throw SubFatal("tag", "exists", ("name", tname));
        var target = (o.Args.Count > 1 ? Resolve(repo, o.Args[1]) : repo.Head()) ?? throw Fatal("badRevision", ("rev", "HEAD"));
        bool annotated = o.Has("a") || o.Vals("m").Count > 0 || o.Val("F") != null;
        if (annotated)
        {
            string text;
            if (o.Vals("m").Count > 0) text = string.Join("\n\n", o.Vals("m"));
            else if (o.Val("F") is { } f) text = ReadMessageFile(g, f);
            else text = g.EditorError();
            text = CleanMessage(text);
            var kind = repo.Obj(target).Kind;
            target = repo.Put("tag", $"object {target}\ntype {kind}\ntag {tname}\ntagger {g.Ident("committer")}\n\n{text}");
        }
        repo.UpdateRef("refs/tags/" + tname, target);
        if (old != null && old != target) g.Out(M("updated", ("name", tname), ("commit", Abbrev(old))) + "\n");
        return 0;
    }

    // ---------- merge ----------

    static string MergeLabel(Repo repo, string arg)
    {
        if (repo.ReadRef("refs/heads/" + arg) != null) return $"branch '{arg}'";
        if (repo.ReadRef("refs/tags/" + arg) != null) return $"tag '{arg}'";
        return $"commit '{arg}'";
    }

    /// <summary>Merges theirs into ours (the index and work tree hold ours). Returns (files, conflicted paths, index entries, work tree contents).</summary>
    static (FileMap Result, List<string> Conflicts, List<Entry> Stages, Dictionary<string, (string Mode, byte[] Data)> Worktree)
        ThreeWay(Cmd g, Repo repo, string? @base, string ours, string theirs, string theirLabel, List<string> outLines)
    {
        FileMap B = TreeFiles(repo, @base), O = TreeFiles(repo, ours), T = TreeFiles(repo, theirs);
        var result = new FileMap(StringComparer.Ordinal);
        var stages = new List<Entry>();
        var conflicts = new List<string>();
        var worktree = new Dictionary<string, (string, byte[])>(StringComparer.Ordinal);
        void AddStages(string p, (string Mode, string Sha)? b, (string Mode, string Sha)? o, (string Mode, string Sha)? t)
        {
            foreach (var (n, side) in new[] { (1, b), (2, o), (3, t) })
                if (side is { } sv) stages.Add(new Entry(p, sv.Sha, sv.Mode, n));
        }
        foreach (var p in B.Keys.Union(O.Keys).Union(T.Keys).Order(ByBytes))
        {
            g.Check();
            var b = Get(B, p);
            var o = Get(O, p);
            var t = Get(T, p);
            if (o == t) { if (o is { } ov) result[p] = ov; continue; }
            if (b == o) { if (t is { } tv) result[p] = tv; continue; }
            if (b == t) { if (o is { } ov) result[p] = ov; continue; }
            if (o is { } oo && t is { } tt && oo.Mode != "120000" && tt.Mode != "120000")
            {
                byte[] od = repo.Typed(oo.Sha, "blob"), td = repo.Typed(tt.Sha, "blob");
                var bd = b is { } bb ? repo.Typed(bb.Sha, "blob") : [];
                outLines.Add(Msg("autoMerging", ("path", p)));
                var mode = oo.Mode == tt.Mode ? oo.Mode : (b != null && b.Value.Mode == oo.Mode ? tt.Mode : oo.Mode);
                byte[] merged;
                bool conflict;
                if (IsBinary(od) || IsBinary(td) || IsBinary(bd))
                {
                    outLines.Add(Msg("binaryConflict", ("path", p), ("ours", "HEAD"), ("theirs", theirLabel)));
                    (merged, conflict) = (od, true);
                }
                else (merged, conflict) = MergeLines(bd, od, td, "HEAD", theirLabel, g.Check);
                if (!conflict)
                {
                    result[p] = (mode, repo.Put("blob", merged));
                    continue;
                }
                outLines.Add(Msg(b == null ? "conflictAddAdd" : "conflictContent", ("path", p)));
                conflicts.Add(p);
                AddStages(p, b, o, t);
                worktree[p] = (mode, merged);
                continue;
            }
            // deleted on one side, changed on the other (or a symbolic link changed both ways)
            (string Mode, string Sha) keep;
            if (o != null && t != null)
            {
                outLines.Add(Msg("conflictContent", ("path", p)));
                keep = o.Value;
            }
            else if (o != null)
            {
                outLines.Add(Msg("conflictModifyDelete", ("path", p), ("deleted", theirLabel), ("modified", "HEAD")));
                keep = o.Value;
            }
            else
            {
                outLines.Add(Msg("conflictModifyDelete", ("path", p), ("deleted", "HEAD"), ("modified", theirLabel)));
                keep = t!.Value;
            }
            conflicts.Add(p);
            AddStages(p, b, o, t);
            worktree[p] = (keep.Mode, repo.Typed(keep.Sha, "blob"));
        }
        return (result, conflicts, stages, worktree);
    }

    static bool SameMap(FileMap a, FileMap b) => a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    static int GitMerge(Cmd g, List<string> args)
    {
        var o = Parse("merge", args);
        var repo = g.Repo();
        string M(string key, params (string, string)[] v) => SubMsg("merge", key, v);
        if (o.Has("\x04"))  // --abort
        {
            if (!repo.GExists("MERGE_HEAD")) throw SubFatal("merge", "noMergeToAbort");
            SwitchTrees(g, repo, repo.Head(), repo.Head(), force: true);
            foreach (var f in new[] { "MERGE_HEAD", "MERGE_MSG", "MERGE_MODE" }) repo.GRemove(f);
            return 0;
        }
        if (o.Has("\x05"))  // --continue
        {
            if (!repo.GExists("MERGE_HEAD")) throw SubFatal("merge", "noMergeInProgress");
            return GitCommit(g, ["--no-edit"]);
        }
        var entries = repo.Index();
        if (repo.Unmerged(entries).Count > 0) throw new GitException(M("unmergedFiles"), 128);
        if (repo.GExists("MERGE_HEAD")) throw new GitException(M("mergeHeadExists"), 128);
        if (o.Args.Count == 0) throw SubFatal("merge", "noRemote");
        if (o.Args.Count > 1) throw SubFatal("merge", "octopus");
        var arg = o.Args[0];
        var sha = TryResolve(repo, arg);
        var theirs = sha != null ? Peel(repo, sha, "commit") : null;
        if (theirs == null) throw new GitException(M("notSomething", ("name", arg)), 1);
        var head = repo.Head();
        bool quiet = o.Has("q");
        if (head == null)
        {
            SwitchTrees(g, repo, null, theirs, force: false, verb: "merge");
            repo.UpdateRef("HEAD", theirs, $"merge {arg}: Fast-forward");
            return 0;
        }
        if (theirs == head || Ancestors(repo, [head]).Contains(theirs))
        {
            if (!quiet) g.Out(M("upToDate") + "\n");
            return 0;
        }
        var @base = MergeBase(repo, head, theirs);
        bool ff = @base == head;
        if (o.Has("\x02") && !ff) throw SubFatal("merge", "notFastForward");
        var cur = repo.Branch();
        var label = MergeLabel(repo, arg);
        var message = o.Vals("m").Count > 0 ? string.Join("\n\n", o.Vals("m")) : $"Merge {label}" + (cur != null && cur is not ("master" or "main") ? $" into {cur}" : "");
        if (ff && !o.Has("\x01"))
        {
            if (!quiet) g.Out(M("updating", ("old", Abbrev(head)), ("new", Abbrev(theirs))) + "\n");
            SwitchTrees(g, repo, head, theirs, force: false, verb: "merge");
            repo.GWrite("ORIG_HEAD", head + "\n");
            repo.UpdateRef("HEAD", theirs, $"merge {arg}: Fast-forward");
            if (!quiet)
            {
                g.Out(M("fastForward") + "\n");
                PrintDiffstat(g, repo, head, theirs);
            }
            return 0;
        }
        // a real merge: refuse when the index has staged changes, or local changes touch merged paths
        var idx = IndexFiles(entries);
        var headFiles = TreeFiles(repo, head);
        if (!SameMap(idx, headFiles))
        {
            var d = idx.Keys.Union(headFiles.Keys).Order(GitCore.Ordinal).Where(p => Get(idx, p) != Get(headFiles, p));
            throw new GitException(SubMsg("checkout", "localChanges", ("verb", "merge"), ("paths", Tabbed(d)), ("advice", SubMsg("checkout", "localChangesAdviceMerge"))), 2);
        }
        var lines = new List<string>();
        var (result, conflicts, stages, worktree) = ThreeWay(g, repo, @base, head, theirs, arg, lines);
        var touched = result.Keys.Union(headFiles.Keys).Order(GitCore.Ordinal).Where(p => Get(result, p) != Get(headFiles, p)).Concat(conflicts).ToList();
        var dirty = touched.Where(p => idx.ContainsKey(p) && repo.WtId(p) != idx[p]).ToList();
        var clobber = touched.Where(p => !idx.ContainsKey(p) && repo.WtId(p) != null && !repo.Ignored(p)).ToList();
        if (dirty.Count > 0)
        {
            g.Err(SubMsg("checkout", "localChanges", ("verb", "merge"), ("paths", Tabbed(dirty)), ("advice", SubMsg("checkout", "localChangesAdviceMerge"))) + "\n");
            g.Out(M("strategyFailed") + "\n");
            return 2;
        }
        if (clobber.Count > 0)
            throw new GitException(SubMsg("checkout", "untrackedOverwritten", ("verb", "merge"), ("paths", Tabbed(clobber)), ("advice", SubMsg("checkout", "untrackedAdviceMerge"))), 2);
        foreach (var line in lines) g.Out(line + "\n");
        repo.GWrite("ORIG_HEAD", head + "\n");
        // write the result into the work tree and index
        var newEntries = new List<Entry>();
        foreach (var p in headFiles.Keys.Union(result.Keys).Union(worktree.Keys).Order(GitCore.Ordinal))
        {
            if (worktree.TryGetValue(p, out var wt))
            {
                var bsha = repo.Put("blob", wt.Data);
                repo.WtWrite(p, wt.Mode != "120000" ? wt.Mode : "100644", bsha);
                continue;
            }
            if (Get(result, p) is not { } t) repo.WtRemove(p);
            else
            {
                if (Get(headFiles, p) != t || repo.WtId(p) != t) repo.WtWrite(p, t);
                newEntries.Add(repo.EntryFor(p, t));
            }
        }
        repo.SaveIndex(newEntries.Concat(stages));
        if (conflicts.Count > 0)
        {
            repo.GWrite("MERGE_HEAD", theirs + "\n");
            repo.GWrite("MERGE_MODE", o.Has("\x01") ? "no-ff\n" : "");
            repo.GWrite("MERGE_MSG", message + "\n\n# Conflicts:\n" + string.Concat(conflicts.Select(p => $"#\t{p}\n")));
            g.Out(M("failed") + "\n");
            return 1;
        }
        var tree = repo.WriteTree(result);
        var body = $"tree {tree}\nparent {head}\nparent {theirs}\nauthor {g.Ident("author")}\ncommitter {g.Ident("committer")}\n\n" + CleanMessage(message);
        var newSha = repo.Put("commit", body);
        repo.UpdateRef("HEAD", newSha, $"merge {arg}: Merge made by the 'ort' strategy.");
        if (!quiet)
        {
            g.Out(M("madeByOrt") + "\n");
            PrintDiffstat(g, repo, head, newSha);
        }
        return 0;
    }

    static void PrintDiffstat(Cmd g, Repo repo, string old, string @new)
    {
        FileMap a = TreeFiles(repo, old), b = TreeFiles(repo, @new);
        var changes = DiffFiles(a, b, load: TreeLoader(repo, a, b));
        if (changes.Count > 0) WriteStat(g.Out, StatEntries(g, repo, a, b, changes));
        foreach (var line in SummaryLines(changes, a, b)) g.Out(line + "\n");
    }

    // ---------- stash ----------

    /// <summary>[(commit, message)], newest first, from the stash's reflog.</summary>
    static List<(string Sha, string Message)> StashEntries(Repo repo)
    {
        var output = new List<(string, string)>();
        foreach (var line in (repo.GText("logs/refs/stash") ?? "").Split('\n'))
        {
            var m = Regex.Match(line, @"^[0-9a-f]{40} ([0-9a-f]{40}) [^\t]*\t(.*)$");
            if (m.Success) output.Add((m.Groups[1].Value, m.Groups[2].Value));
        }
        output.Reverse();
        return output;
    }

    static void WriteStash(Repo repo, List<(string Sha, string Message)> entries)
    {
        if (entries.Count == 0)
        {
            repo.GRemove("refs/stash");
            repo.GRemove("logs/refs/stash");
            return;
        }
        var lines = new StringBuilder();
        var prev = Z40;
        var who = repo.G.Ident("committer", required: false);
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            lines.Append($"{prev} {entries[i].Sha} {who}\t{entries[i].Message}\n");
            prev = entries[i].Sha;
        }
        repo.GWrite("logs/refs/stash", lines.ToString());
        repo.GWrite("refs/stash", entries[0].Sha + "\n");
    }

    static (int N, string Sha) StashRef(Repo repo, string? arg)
    {
        var entries = StashEntries(repo);
        if (entries.Count == 0) throw new GitException(SubMsg("stash", "noEntries"), 1);
        long n = 0;
        if (arg != null)
        {
            var m = Regex.Match(arg, @"^(?:stash@\{)?([0-9]+)\}?\z");
            if (!m.Success) throw new GitException(SubMsg("stash", "notStash", ("name", arg)), 1);
            n = long.TryParse(m.Groups[1].Value, out var x) ? x : long.MaxValue;
        }
        if (n >= entries.Count) throw new GitException(SubMsg("stash", "noSuchEntry", ("name", $"stash@{{{n}}}")), 1);
        return ((int)n, entries[(int)n].Sha);
    }

    static int GitStash(Cmd g, List<string> args)
    {
        var action = "push";
        if (args.Count > 0 && !args[0].StartsWith('-')) (action, args) = (args[0], args[1..]);
        if (action is not ("push" or "save" or "list" or "show" or "pop" or "apply" or "drop" or "clear"))
            throw UsageError("stash", SubMsg("stash", "unknownAction", ("action", action)));
        var o = Parse("stash", args);
        var repo = g.Repo();
        string M(string key, params (string, string)[] v) => SubMsg("stash", key, v);
        if (action is "push" or "save")
        {
            var head = repo.Head() ?? throw new GitException(M("noInitialCommit"), 1);
            if (repo.Unmerged().Count > 0) throw new GitException(M("unmerged"), 1);
            var given = o.Vals("m").Count > 0 ? string.Join("\n\n", o.Vals("m")) : (action == "save" && o.Args.Count > 0 ? string.Join(" ", o.Args) : null);
            var st = new Status(repo, o.Has("u") ? "all" : "no");
            var specs = action == "push" ? UserSpecs(repo, o.Operands) : null;
            var headFiles = TreeFiles(repo, head);
            if (specs != null)
            {
                var known = IndexFiles(repo.Index()).Keys.Union(headFiles.Keys).Union(st.Untracked).ToList();
                var ops = o.Operands;
                for (int k = 0; k < specs.Count; k++)
                    if (!known.Any(p => PathspecMatch([specs[k]], p))) throw new GitException(Msg("pathspecNoMatchKnown", ("path", ops[k])), 1);
                st.Staged = st.Staged.Where(c => PathspecMatch(specs, c.New ?? c.Old!) || (c.Old != null && PathspecMatch(specs, c.Old))).ToList();
                st.Unstaged = st.Unstaged.Where(c => PathspecMatch(specs, c.Old!)).ToList();
                st.Untracked = st.Untracked.Where(u => PathspecMatch(specs, u)).ToList();
            }
            if (st.Staged.Count == 0 && st.Unstaged.Count == 0 && !(o.Has("u") && st.Untracked.Count > 0))
            {
                if (!o.Has("q")) g.Out(M("noLocalChanges") + "\n");
                return 0;
            }
            var b = repo.Branch() ?? "(no branch)";
            var desc = $"{b}: {SubjectOf(repo, head)}";
            var message = !string.IsNullOrEmpty(given) ? M("onMessage", ("branch", b), ("message", given)) : M("wipMessage", ("description", desc));
            var ident = g.Ident("committer");
            var author = g.Ident("author");
            var idxFiles = IndexFiles(repo.Index());
            HashSet<string>? chosen = null;
            if (specs != null)  // only the named paths change: everything else stays as HEAD has it
            {
                chosen = idxFiles.Keys.Union(headFiles.Keys).Where(p => PathspecMatch(specs, p)).ToHashSet(StringComparer.Ordinal);
                var mixed = new FileMap(StringComparer.Ordinal);
                foreach (var (p, v) in headFiles) if (!chosen.Contains(p)) mixed[p] = v;
                foreach (var (p, v) in idxFiles) if (chosen.Contains(p)) mixed[p] = v;
                idxFiles = mixed;
            }
            var itree = repo.WriteTree(idxFiles);
            var icommit = repo.Put("commit", $"tree {itree}\nparent {head}\nauthor {author}\ncommitter {ident}\n\n{M("indexMessage", ("description", desc))}\n");
            var work = new FileMap(idxFiles, StringComparer.Ordinal);
            int n0 = 0;
            foreach (var p in idxFiles.Keys)
            {
                if (n0++ % 256 == 255) g.Check();
                if (chosen != null && !chosen.Contains(p)) continue;
                if (repo.WtStat(p) is not { } w) work.Remove(p);
                else work[p] = (w.Mode, repo.Put("blob", w.Data));
            }
            var parents = new List<string> { head, icommit };
            if (o.Has("u") && st.Untracked.Count > 0)
            {
                var uf = new FileMap(StringComparer.Ordinal);
                int n1 = 0;
                foreach (var p in st.Untracked)
                {
                    if (n1++ % 256 == 255) g.Check();
                    if (repo.WtStat(p) is { } w) uf[p] = (w.Mode, repo.Put("blob", w.Data));
                }
                var utree = repo.WriteTree(uf);
                parents.Add(repo.Put("commit", $"tree {utree}\nauthor {author}\ncommitter {ident}\n\n{M("untrackedMessage", ("description", desc))}\n"));
            }
            var wtree = repo.WriteTree(work);
            var wcommit = repo.Put("commit", $"tree {wtree}\n" + string.Concat(parents.Select(p => $"parent {p}\n")) + $"author {author}\ncommitter {ident}\n\n{message}");
            WriteStash(repo, [(wcommit, message), .. StashEntries(repo)]);
            if (chosen == null) SwitchTrees(g, repo, head, head, force: true);
            else
            {
                var byPath = repo.Stage0();
                foreach (var p in chosen.Order(GitCore.Ordinal))
                {
                    if (Get(headFiles, p) is not { } h)
                    {
                        byPath.Remove(p);
                        repo.WtRemove(p);
                        continue;
                    }
                    if (repo.WtId(p) != h) repo.WtWrite(p, h);
                    byPath[p] = repo.EntryFor(p, h);
                }
                repo.SaveIndex(byPath.Values);
            }
            if (o.Has("u")) foreach (var p in st.Untracked) repo.WtRemove(p);
            if (!o.Has("q")) g.Out(M("saved", ("message", message)) + "\n");
            return 0;
        }
        if (action == "list")
        {
            var all = StashEntries(repo);
            for (int i = 0; i < all.Count; i++) g.Out($"stash@{{{i}}}: {all[i].Message}\n");
            return 0;
        }
        if (action == "clear")
        {
            WriteStash(repo, []);
            return 0;
        }
        var arg = o.Args.Count > 0 ? o.Args[0] : null;
        var (n, sha) = StashRef(repo, arg);
        if (action == "show")
        {
            var sc = repo.Commit(sha);
            FileMap a0 = TreeFiles(repo, sc.Parents[0]), b0 = repo.Flatten(sc.Tree);
            if (!(o.Has("p") || o.Has("\x03") || o.Has("\x04"))) o.Flags["\x01"] = 1;
            WriteDiff(g, repo, a0, b0, o);
            return 0;
        }
        if (action == "drop") return StashDrop(g, repo, n, sha, o.Has("q"), arg);
        // apply / pop
        var c = repo.Commit(sha);
        var stashBase = c.Parents[0];
        var lines = new List<string>();
        FileMap stashFiles = repo.Flatten(c.Tree), baseFiles = TreeFiles(repo, stashBase);
        var untrackedFiles = c.Parents.Count > 2 ? repo.Flatten(repo.Commit(c.Parents[2]).Tree) : new FileMap(StringComparer.Ordinal);
        foreach (var p in untrackedFiles.Keys)
            if (repo.WtStat(p) != null) throw new GitException(M("untrackedExists", ("path", p)), 1);
        var entries = repo.Index();
        var idx = repo.Stage0(entries);
        var cur = IndexFiles(entries);
        var paths = stashFiles.Keys.Union(baseFiles.Keys).ToList();
        var workNow = new Dictionary<string, (string, string)?>(StringComparer.Ordinal);
        foreach (var p in paths) workNow[p] = repo.WtId(p);
        var dirty = paths.Order(GitCore.Ordinal).Where(p => Get(stashFiles, p) != Get(baseFiles, p) && cur.ContainsKey(p) && workNow[p] != Get(cur, p)).ToList();
        if (dirty.Count > 0)
            throw new GitException(SubMsg("checkout", "localChanges", ("verb", "merge"), ("paths", Tabbed(dirty)), ("advice", SubMsg("checkout", "localChangesAdviceMerge")))
                + "\n" + M("cannotApply"), 1);
        var conflicts = new List<string>();
        if (c.Tree == repo.Commit(stashBase).Tree) g.Out(M("upToDate") + "\n");
        foreach (var p in paths.Order(GitCore.Ordinal))
        {
            var s = Get(stashFiles, p);
            var b = Get(baseFiles, p);
            if (s == b) continue;
            var mine = Get(cur, p);
            if (mine == b || mine == s)
            {
                if (s is not { } sv)
                {
                    repo.WtRemove(p);
                    idx.Remove(p);
                }
                else
                {
                    repo.WtWrite(p, sv);
                    if (b == null) idx[p] = repo.EntryFor(p, sv);
                }
                continue;
            }
            if (s == null || mine == null)
            {
                string up = M("labelUpstream"), stashed = M("labelStashed");
                lines.Add(Msg("conflictModifyDelete", ("path", p), ("deleted", mine == null ? up : stashed), ("modified", mine == null ? stashed : up)));
                conflicts.Add(p);
                continue;
            }
            lines.Add(Msg("autoMerging", ("path", p)));
            var (merged, conflict) = MergeLines(b is { } bv ? repo.Typed(bv.Sha, "blob") : [], repo.Typed(mine.Value.Sha, "blob"), repo.Typed(s.Value.Sha, "blob"),
                M("labelUpstream"), M("labelStashed"), g.Check);
            repo.WtWrite(p, s.Value.Mode, repo.Put("blob", merged));
            if (conflict)
            {
                lines.Add(Msg("conflictContent", ("path", p)));
                conflicts.Add(p);
                idx.Remove(p);
                foreach (var (k, side) in new[] { (1, b), (2, mine), (3, s) })
                    if (side is { } sd) entries.Add(new Entry(p, sd.Sha, sd.Mode, k));
            }
        }
        foreach (var (p, t) in untrackedFiles) repo.WtWrite(p, t);
        repo.SaveIndex(idx.Values.Concat(entries.Where(e => e.Stage != 0 && conflicts.Contains(e.Path))).ToList());
        foreach (var line in lines) g.Out(line + "\n");
        if (conflicts.Count > 0)
        {
            g.Out(LongStatus(repo, new Status(repo)));
            if (action == "pop") g.Out(M("keptEntry") + "\n");
            return 1;
        }
        if (!o.Has("q")) g.Out(LongStatus(repo, new Status(repo)));
        if (action == "pop") return StashDrop(g, repo, n, sha, o.Has("q"), arg);
        return 0;
    }

    static int StashDrop(Cmd g, Repo repo, int n, string sha, bool quiet, string? given = null)
    {
        var entries = StashEntries(repo);
        entries.RemoveAt(n);
        WriteStash(repo, entries);
        if (!quiet) g.Out(SubMsg("stash", "dropped", ("name", !string.IsNullOrEmpty(given) ? given : $"refs/stash@{{{n}}}"), ("commit", sha)) + "\n");
        return 0;
    }

    // ---------- plumbing and inspection ----------

    static int GitRevParse(Cmd g, List<string> args)
    {
        g.FindRepo();
        int? shortLen = null;
        bool abbrevRef = false, verify = false, quiet = false, symbolic = false;
        var outputs = new List<string>();
        int revs = 0;
        foreach (var a in args)
        {
            if (a is "--is-inside-work-tree" or "--is-inside-git-dir" or "--show-toplevel" or "--git-dir" or "--show-prefix" or "--absolute-git-dir"
                or "--show-cdup" or "--is-bare-repository" or "--git-common-dir")
            {
                var r0 = g.Repo();
                outputs.Add(a switch
                {
                    "--is-inside-work-tree" => "true",
                    "--is-inside-git-dir" => "false",
                    "--show-toplevel" => r0.Top,
                    "--git-dir" or "--git-common-dir" => r0.Prefix.Length == 0 ? ".git" : r0.GitDir,
                    "--absolute-git-dir" => r0.GitDir,
                    "--show-prefix" => r0.Prefix,
                    "--is-bare-repository" => "false",
                    _ => string.Concat(Enumerable.Repeat("../", r0.Prefix.Count(ch => ch == '/'))),
                });
                continue;
            }
            var m = Regex.Match(a, @"^--short(?:=([0-9]+))?\z");
            if (m.Success)
            {
                long want = m.Groups[1].Success ? (long.TryParse(m.Groups[1].Value, out var x) ? x : 40) : 7;
                shortLen = (int)Math.Max(4, Math.Min(40, want));
                continue;
            }
            if (a == "--abbrev-ref") { abbrevRef = true; continue; }
            if (a == "--symbolic-full-name") { symbolic = true; continue; }
            if (a == "--verify") { verify = true; continue; }
            if (a is "-q" or "--quiet") { quiet = true; continue; }
            if (a == "--") continue;
            if (a.StartsWith('-')) { outputs.Add(a); continue; }
            var r = g.Repo();
            revs++;
            var sha = TryResolve(r, a);
            if (sha == null)
            {
                if (verify)
                {
                    if (quiet)
                    {
                        g.Out(string.Concat(outputs.Select(x => x + "\n")));
                        return 1;
                    }
                    throw SubFatal("rev-parse", "needSingleRevision");
                }
                g.Err(Msg("badRevision", ("rev", a)) + "\n");
                g.Out(string.Concat(outputs.Select(x => x + "\n")) + a + "\n");
                return 128;
            }
            if (abbrevRef || symbolic)
            {
                var prev = Regex.Match(a, @"^@\{-([0-9]+)\}\z");
                if (prev.Success && abbrevRef)
                {
                    outputs.Add((int.TryParse(prev.Groups[1].Value, out var pn) ? PreviousBranch(r, pn) : null) ?? a);
                    continue;
                }
                string? name = null;
                if (a is "HEAD" or "@")
                {
                    var hr = r.HeadRef();
                    name = hr != null ? (abbrevRef && hr.StartsWith("refs/heads/") ? hr[11..] : hr) : "HEAD";
                }
                else if (r.ReadRef("refs/heads/" + a) != null) name = abbrevRef ? a : "refs/heads/" + a;
                else if (r.ReadRef("refs/tags/" + a) != null) name = abbrevRef ? (r.ReadRef("refs/heads/" + a) != null ? "tags/" + a : a) : "refs/tags/" + a;
                outputs.Add(name ?? (abbrevRef ? a : ""));
                continue;
            }
            outputs.Add(shortLen is { } sl ? sha[..sl] : sha);
        }
        if (verify && revs != 1) throw SubFatal("rev-parse", "needSingleRevision");
        g.Out(string.Concat(outputs.Select(x => x + "\n")));
        return 0;
    }

    static int GitLsFiles(Cmd g, List<string> args)
    {
        var o = Parse("ls-files", args);
        var repo = g.Repo();
        var specs = UserSpecs(repo, o.Operands);
        if (specs == null && repo.Prefix.Length > 0) specs = [repo.Prefix.TrimEnd('/')];
        if (o.Has("\x02")) repo.Prefix = "";
        var entries = repo.Index();
        var output = new List<string>();
        var modes = new[] { "c", "o", "m", "d", "u", "s" }.Where(o.Has).ToList();
        string Long(Entry e) => $"{e.Mode} {e.Sha} {e.Stage}\t{CQuote(repo.Show(e.Path))}";
        if (modes.Count == 0 || o.Has("c") || (o.Has("s") && !o.Has("o")))
        {
            foreach (var e in entries)
                if (PathspecMatch(specs, e.Path) && (!o.Has("u") || e.Stage != 0))
                    output.Add(o.Has("s") || o.Has("u") ? Long(e) : CQuote(repo.Show(e.Path)));
            if (!o.Has("s") && !o.Has("u")) output = output.Distinct().ToList();
        }
        if (o.Has("u") && !o.Has("c") && !o.Has("s")) output = entries.Where(e => e.Stage != 0 && PathspecMatch(specs, e.Path)).Select(Long).ToList();
        if (o.Has("d") || o.Has("m"))
            foreach (var (p, id) in IndexFiles(entries))
            {
                if (!PathspecMatch(specs, p)) continue;
                var w = repo.WtId(p);
                if ((o.Has("d") && w == null) || (o.Has("m") && w != id)) output.Add(CQuote(repo.Show(p)));
            }
        if (o.Has("o"))
        {
            var tracked = entries.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
            foreach (var p in repo.Walk(""))
            {
                if (tracked.Contains(p) || !PathspecMatch(specs, p.TrimEnd('/'))) continue;
                if (o.Has("\x01") && repo.Ignored(p.TrimEnd('/'), p.EndsWith('/'))) continue;
                output.Add(CQuote(repo.Show(p)));
            }
            output = output.Order(ByBytes).ToList();
        }
        foreach (var line in output) g.Out(line + "\n");
        return 0;
    }

    static int GitCatFile(Cmd g, List<string> args)
    {
        var o = Parse("cat-file", args);
        var repo = g.Repo();
        var modes = new[] { "t", "s", "p", "e" }.Where(o.Has).ToList();
        if (modes.Count > 1 || (modes.Count == 0 && o.Args.Count != 2) || (modes.Count > 0 && o.Args.Count != 1))
            throw UsageError("cat-file", SubMsg("cat-file", "badUsage"));
        var name = o.Args[^1];
        var sha = TryResolve(repo, name);
        if (sha == null)
        {
            if (modes is ["e"]) return 1;
            throw SubFatal("cat-file", "notValid", ("name", name));
        }
        var (kind, data) = repo.Obj(sha);
        if (modes.Count == 0)
        {
            var want = o.Args[0];
            if (want is not ("blob" or "tree" or "commit" or "tag")) throw SubFatal("cat-file", "badType", ("kind", want));
            var sha2 = Peel(repo, sha, want) ?? throw SubFatal("cat-file", "badFile", ("name", name));
            g.Out(ByteText.Decode(repo.Obj(sha2).Data));
            return 0;
        }
        if (o.Has("e")) return 0;
        if (o.Has("t")) g.Out(kind + "\n");
        else if (o.Has("s")) g.Out($"{data.Length}\n");
        else if (kind == "tree")
            foreach (var (mode, n, s) in repo.Tree(sha))
                g.Out($"{mode} {(mode == "040000" ? "tree" : mode == "160000" ? "commit" : "blob")} {s}\t{CQuote(n)}\n");
        else g.Out(ByteText.Decode(data));
        return 0;
    }

    static int GitRevList(Cmd g, List<string> args)
    {
        var o = Parse("rev-list", NumericShorthand(args));
        var repo = g.Repo();
        var (include, exclude, paths) = RevisionsAndPaths(repo, o);
        if (o.Has("\x02")) include.AddRange(AllRefCommits(repo));
        if (include.Count == 0) throw UsageError("rev-list", SubMsg("rev-list", "noRevisions"));
        var excluded = exclude.Count > 0 ? Ancestors(repo, exclude) : [];
        var order = WalkOrder(repo, include.Distinct()).Where(c => !excluded.Contains(c)).ToList();
        var specs = UserSpecs(repo, paths);
        if (specs != null)
            order = order.Where(c =>
            {
                var cm = repo.Commit(c);
                return DiffFiles(TreeFiles(repo, cm.Parents.Count > 0 ? cm.Parents[0] : null), repo.Flatten(cm.Tree), specs).Count > 0;
            }).ToList();
        if (o.Val("n") is { } nv && Regex.IsMatch(nv, @"^[0-9]+\z")) order = order.Take(int.TryParse(nv, out var take) ? take : int.MaxValue).ToList();
        g.Out(o.Has("\x01") ? $"{order.Count}\n" : string.Concat(order.Select(c => c + "\n")));
        return 0;
    }

    static int GitClean(Cmd g, List<string> args)
    {
        var o = Parse("clean", args);
        var repo = g.Repo();
        var forceCfg = ConfigBool(g.Config("clean.requireforce"));
        if (!(o.Has("f") || o.Has("n")) && forceCfg != false) throw SubFatal("clean", "requireForce");
        var specs = UserSpecs(repo, o.Operands);
        var tracked = repo.Index().Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        List<string> cands;
        if (o.Has("X")) cands = repo.Walk("").Where(p => !tracked.Contains(p) && repo.Ignored(p.TrimEnd('/'), p.EndsWith('/'))).ToList();
        else if (o.Has("x"))
        {
            repo.NoIgnore = true;
            try { cands = Untracked(repo, tracked, "normal"); }
            finally { repo.NoIgnore = false; }
        }
        else cands = Untracked(repo, tracked, "normal");
        foreach (var p in cands)
        {
            if (!PathspecMatch(specs, p.TrimEnd('/'))) continue;
            if (p.EndsWith('/') && !o.Has("d")) continue;  // without -d only the untracked files directly in tracked directories go
            if (p.EndsWith('/') && repo.Fs.Exists(repo.Wt(p) + ".git", false)) continue;
            if (o.Has("n"))
            {
                g.Out(SubMsg("clean", "wouldRemove", ("path", repo.Show(p))) + "\n");
                continue;
            }
            if (!o.Has("q")) g.Out(SubMsg("clean", "removing", ("path", repo.Show(p))) + "\n");
            repo.Fs.Delete(repo.Wt(p.TrimEnd('/')), recursive: true);
        }
        return 0;
    }

    static int GitRemote(Cmd g, List<string> args)
    {
        var o = Parse("remote", args);
        g.Repo();
        if (o.Args.Count > 0) throw SubFatal("remote", "noRemotes");
        return 0;  // there are no remotes to list
    }

    // ---------- config ----------

    static int GitConfig(Cmd g, List<string> args)
    {
        var o = Parse("config", args);
        string M(string k, params (string, string)[] v) => SubMsg("config", k, v);
        var scope = o.Has("\x01") ? "global" : o.Has("\x02") ? "local" : null;
        if (o.Has("l"))
        {
            if (scope is "global" or null)
                foreach (var (n, v, _) in g.GlobalConfig().Config.Items()) g.Out($"{n}={v}\n");
            if (scope != "global")
            {
                var r = g.FindRepo();
                if (r == null && scope == "local") throw SubFatal("config", "notInGitDir");
                if (r != null) foreach (var (n, v, _) in r.Config().Items()) g.Out($"{n}={v}\n");
            }
            if (scope == null) foreach (var (n, v) in g.Overrides) g.Out($"{n}={v}\n");
            return 0;
        }
        if (o.Has("\x03"))  // --unset
        {
            if (o.Args.Count != 1) throw UsageError("config", M("wrongArgs"));
            return ConfigWrite(g, scope, CanonicalKey(o.Args[0]), null);
        }
        bool get = o.Has("\x04");
        if (o.Args.Count == 0 || o.Args.Count > 2) throw UsageError("config", M("wrongArgs"));
        var name = o.Args[0];
        if (!ValidKey(name))
        {
            g.Err(M(!name.Contains('.') ? "noSection" : "invalidKey", ("name", name)) + "\n");
            return 1;
        }
        var key = CanonicalKey(name);
        if (o.Args.Count == 1 || get)
        {
            string? value;
            if (scope == "global") value = g.GlobalConfig().Config.Get(key);
            else if (scope == "local") value = (g.FindRepo() ?? throw SubFatal("config", "notInGitDir")).Config().Get(key);
            else value = g.Config(key);
            if (value == null) return 1;
            if (o.Has("\x05"))
            {
                var b = ConfigBool(value) ?? throw SubFatal("config", "badBool", ("value", value), ("name", key));
                value = b ? "true" : "false";
            }
            g.Out(value + "\n");
            return 0;
        }
        return ConfigWrite(g, scope, key, o.Args[1]);
    }

    static int ConfigWrite(Cmd g, string? scope, string key, string? value)
    {
        if (scope == "global")
        {
            var (path, cfg) = g.GlobalConfig();
            bool changed = cfg.Set(key, value);
            if (value == null && !changed) return 5;
            g.Ctx.Fs.WriteAllBytes(path, Encoding.UTF8.GetBytes(cfg.Text()));
            return 0;
        }
        var r = g.FindRepo() ?? throw SubFatal("config", "notInGitDir");
        if (!r.Config().Set(key, value) && value == null) return 5;
        r.SaveConfig();
        return 0;
    }
}
