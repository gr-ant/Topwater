using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using static Topwater.GitCore;
using FileMap = System.Collections.Generic.Dictionary<string, (string Mode, string Sha)>;

namespace Topwater;

internal static partial class Git
{
    // ---------- patches: apply, am, format-patch ----------

    /// <summary>One file's part of a unified diff. Old/New are paths (null: /dev/null); hunks are
    /// (old start, old count, new start, new count, [(sign, line)]), lines as Latin-1 strings (one char per byte).</summary>
    sealed class FilePatch
    {
        public string? Old, New, OldMode, NewMode;
        public int Similarity = 100;
        public bool Renamed, Binary;
        public List<(int Os, int Oc, int Ns, int Nc, List<(char Sign, string Text)> Lines)> Hunks = [];

        public (int Ins, int Dels) Counts() =>
            (Hunks.Sum(h => h.Lines.Count(l => l.Sign == '+')), Hunks.Sum(h => h.Lines.Count(l => l.Sign == '-')));

        public void Reverse()
        {
            (Old, New, OldMode, NewMode) = (New, Old, NewMode, OldMode);
            Hunks = Hunks.Select(h => (h.Ns, h.Nc, h.Os, h.Oc, h.Lines.Select(l => (l.Sign switch { '+' => '-', '-' => '+', var s => s }, l.Text)).ToList())).ToList();
        }
    }

    /// <summary>A name as git quotes it ("a\tb", octal escapes for other bytes) back to the name.</summary>
    static string CUnquote(string s)
    {
        if (!(s.Length >= 2 && s[0] == '"' && s[^1] == '"')) return s;
        var output = new List<byte>();
        var body = s[1..^1];
        for (int i = 0; i < body.Length;)
        {
            char c = body[i];
            if (c == '\\' && i + 1 < body.Length)
            {
                char n = body[i + 1];
                if (i + 4 <= body.Length && Regex.IsMatch(body.Substring(i + 1, 3), @"\A[0-7]{3}\z"))
                {
                    output.Add((byte)Convert.ToInt32(body.Substring(i + 1, 3), 8));
                    i += 4;
                    continue;
                }
                output.AddRange(n switch
                {
                    'a' => [7], 'b' => [8], 't' => [9], 'n' => [10], 'v' => [11], 'f' => [12], 'r' => [13],
                    _ => ByteText.Encode(n.ToString()),
                });
                i += 2;
                continue;
            }
            int len = char.IsHighSurrogate(c) && i + 1 < body.Length ? 2 : 1;
            output.AddRange(ByteText.Encode(body.Substring(i, len)));
            i += len;
        }
        return ByteText.Decode(output.ToArray());
    }

    /// <summary>The file patches in a unified diff (git's extended headers or plain diff -u). Text around them, such as a
    /// mail's message or signature, is skipped. A hunk cut short or with a stray line is a corrupt patch.</summary>
    static List<FilePatch> ParsePatch(byte[] data, int strip)
    {
        var lines = SplitLines(data);
        int n = lines.Count;
        string TextAt(int k) => Text(lines[k]).TrimEnd('\n');
        static string? Name(string raw, int cut)
        {
            if (!raw.StartsWith('"')) raw = raw.Split('\t', 2)[0];
            raw = CUnquote(raw.TrimEnd());
            if (raw == "/dev/null") return null;
            var parts = raw.Split('/');
            return parts.Length > cut ? string.Join("/", parts[cut..]) : raw;
        }
        GitException Corrupt(int k) => SubFatal("apply", "corrupt", ("line", (k + 1).ToString()));
        var output = new List<FilePatch>();
        int i = 0;
        while (i < n)
        {
            var t = TextAt(i);
            FilePatch fp;
            if (t.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                fp = new FilePatch();
                var rest = t[11..];
                var m = Regex.Match(rest, @"\A(""(?:[^""\\]|\\.)*""|\S+) (""(?:[^""\\]|\\.)*""|\S+)\z");
                if (!m.Success) m = Regex.Match(rest, @"\A(a/.*) (b/.*)\z");
                if (m.Success) (fp.Old, fp.New) = (Name(m.Groups[1].Value, strip), Name(m.Groups[2].Value, strip));
                i++;
                while (i < n && !(TextAt(i) is var x && (x.StartsWith("diff --git ", StringComparison.Ordinal) || x.StartsWith("--- ", StringComparison.Ordinal) || x.StartsWith("@@ ", StringComparison.Ordinal))))
                {
                    var h = TextAt(i);
                    if (h.StartsWith("new file mode ", StringComparison.Ordinal)) (fp.Old, fp.NewMode) = (null, h[14..]);
                    else if (h.StartsWith("deleted file mode ", StringComparison.Ordinal)) (fp.New, fp.OldMode) = (null, h[18..]);
                    else if (h.StartsWith("old mode ", StringComparison.Ordinal)) fp.OldMode = h[9..];
                    else if (h.StartsWith("new mode ", StringComparison.Ordinal)) fp.NewMode = h[9..];
                    else if (h.StartsWith("rename from ", StringComparison.Ordinal) || h.StartsWith("copy from ", StringComparison.Ordinal))
                        (fp.Old, fp.Renamed) = (CUnquote(h.Split(' ', 3)[2]), h.StartsWith("rename", StringComparison.Ordinal));
                    else if (h.StartsWith("rename to ", StringComparison.Ordinal) || h.StartsWith("copy to ", StringComparison.Ordinal))
                        fp.New = CUnquote(h.Split(' ', 3)[2]);
                    else if (h.StartsWith("similarity index ", StringComparison.Ordinal))
                        fp.Similarity = h[17..].TrimEnd('%') is { Length: > 0 } sv && sv.All(char.IsAsciiDigit) && int.TryParse(sv, out var sim) ? sim : 0;
                    else if (h.StartsWith("Binary files ", StringComparison.Ordinal) || h.StartsWith("GIT binary patch", StringComparison.Ordinal)) fp.Binary = true;
                    else if (!h.StartsWith("index ", StringComparison.Ordinal) && !h.StartsWith("dissimilarity index ", StringComparison.Ordinal)) break;
                    i++;
                }
                if (i < n && TextAt(i).StartsWith("--- ", StringComparison.Ordinal))
                {
                    if (i + 1 >= n || !TextAt(i + 1).StartsWith("+++ ", StringComparison.Ordinal)) throw Corrupt(i + 1);
                    string? old = Name(TextAt(i)[4..], strip), @new = Name(TextAt(i + 1)[4..], strip);
                    fp.Old = old == null || !fp.Renamed ? old : fp.Old;
                    fp.New = @new == null || !fp.Renamed ? @new : fp.New;
                    i += 2;
                }
            }
            else if (t.StartsWith("--- ", StringComparison.Ordinal) && i + 1 < n && TextAt(i + 1).StartsWith("+++ ", StringComparison.Ordinal))
            {
                fp = new FilePatch();
                string? old = Name(t[4..], strip), @new = Name(TextAt(i + 1)[4..], strip);
                fp.Old = old == null ? null : @new ?? old;  // plain diffs patch the +++ file
                fp.New = @new;
                i += 2;
            }
            else
            {
                i++;
                continue;
            }
            while (i < n && TextAt(i).StartsWith("@@ ", StringComparison.Ordinal))
            {
                var m = Regex.Match(TextAt(i), @"\A@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@");
                if (!m.Success) throw Corrupt(i);
                static int Num(Group gr) => gr.Success ? int.Parse(gr.Value) : 1;
                int os = int.Parse(m.Groups[1].Value), oc = Num(m.Groups[2]), ns = int.Parse(m.Groups[3].Value), nc = Num(m.Groups[4]);
                i++;
                var body = new List<(char, string)>();
                int ro = oc, rn = nc;
                while (ro > 0 || rn > 0)
                {
                    if (i >= n) throw Corrupt(i);
                    var line = lines[i];
                    char sign = line is "\n" or "\r\n" ? '\0' : line[0];
                    if (sign == '\\' && body.Count > 0)
                    {
                        body[^1] = (body[^1].Item1, body[^1].Item2.TrimEnd('\n'));
                        i++;
                        continue;
                    }
                    if (sign is '\0' or ' ') (ro, rn) = (ro - 1, rn - 1);
                    else if (sign == '-') ro--;
                    else if (sign == '+') rn--;
                    else throw Corrupt(i);
                    if (ro < 0 || rn < 0) throw Corrupt(i);
                    body.Add((sign == '\0' ? ' ' : sign, sign != '\0' ? line[1..] : line));
                    i++;
                }
                if (i < n && lines[i].StartsWith('\\') && body.Count > 0)
                {
                    body[^1] = (body[^1].Item1, body[^1].Item2.TrimEnd('\n'));
                    i++;
                }
                fp.Hunks.Add((os, oc, ns, nc, body));
            }
            output.Add(fp);
        }
        return output;
    }

    sealed class ApplyFailed : Exception;

    /// <summary>data with fp's hunks applied. Each hunk's lines must match exactly, though it may sit at an offset from the line
    /// the patch names (nearest first); there is no fuzz.</summary>
    static byte[] ApplyHunks(Cmd g, byte[] data, FilePatch fp, string path, bool verbose)
    {
        var lines = SplitLines(data);
        var output = new List<string>();
        int pos = 0, offset = 0;
        for (int k = 0; k < fp.Hunks.Count; k++)
        {
            g.Check();
            var (os, oc, _, _, body) = fp.Hunks[k];
            var pre = body.Where(l => l.Sign != '+').Select(l => l.Text).ToList();
            var post = body.Where(l => l.Sign != '-').Select(l => l.Text).ToList();
            int start = (oc > 0 ? os - 1 : os) + offset;
            int? found = null;
            bool Fits(int p) => pos <= p && p <= lines.Count - pre.Count && lines.Skip(p).Take(pre.Count).SequenceEqual(pre);
            for (int d = 0; d <= Math.Max(lines.Count, 1) && found == null; d++)
                foreach (var p in d > 0 ? [start + d, start - d] : new[] { start })
                    if (Fits(p)) { found = p; break; }
            if (found is not { } at)
            {
                if (verbose) g.Err(SubMsg("apply", "searching", ("lines", Text(string.Concat(pre)))) + "\n");
                g.Err(SubMsg("apply", "patchFailed", ("path", path), ("line", os.ToString())) + "\n");
                throw new ApplyFailed();
            }
            int moved = at - (oc > 0 ? os - 1 : os);
            if (moved != 0 && verbose)
                g.Err(SubMsg("apply", "hunkOffset", ("number", (k + 1).ToString()), ("line", (at + 1).ToString()),
                    ("offset", $"{moved} line{(Math.Abs(moved) != 1 ? "s" : "")}")) + "\n");
            output.AddRange(lines.GetRange(pos, at - pos));
            output.AddRange(post);
            (pos, offset) = (at + pre.Count, moved);
        }
        output.AddRange(lines.Skip(pos));
        return Encoding.Latin1.GetBytes(string.Concat(output));
    }

    /// <summary>Checks every file patch against the work tree and/or the index, then writes them all, or nothing when one fails
    /// (printing why). index and worktree: git apply --index; index only: --cached.</summary>
    static bool ApplyPatches(Cmd g, Repo? repo, string top, List<FilePatch> patches, bool index, bool worktree, bool verbose, bool check)
    {
        var fs = g.Ctx.Fs;
        var entries = repo != null && index ? repo.Index().Where(e => e.Stage == 0).ToDictionary(e => e.Path, StringComparer.Ordinal) : new Dictionary<string, Entry>(StringComparer.Ordinal);
        var state = new Dictionary<string, (string Mode, byte[] Data)?>(StringComparer.Ordinal);  // path -> (mode, data) after the patches so far; null: removed
        (string Mode, byte[] Data)? Current(string p)
        {
            if (state.TryGetValue(p, out var s)) return s;
            if (worktree)
            {
                var full = top.TrimEnd('/') + "/" + p;
                if (repo != null) return repo.WtStat(p);
                if (fs.IsFile(full)) return ((fs.Stat(full).Mode & 0x40) != 0 ? "100755" : "100644", fs.ReadAllBytes(full));
                return null;
            }
            return entries.TryGetValue(p, out var e) ? (e.Mode, repo!.Typed(e.Sha, "blob")) : null;
        }
        bool ok = true;
        foreach (var fp in patches)
        {
            var path = fp.New ?? fp.Old ?? "";
            foreach (var p in new[] { fp.Old, fp.New })
                if (p != null) CheckPath(p);  // both sides: a rename must not reach outside the work tree either
            if (verbose) g.Err(SubMsg("apply", "checking", ("path", path)) + "\n");
            if (fp.Binary)
            {
                g.Err(SubMsg("apply", "binary", ("path", path)) + "\n");
                ok = false;
                continue;
            }
            string? oldMode;
            byte[] oldData;
            try
            {
                if (fp.Old == null)
                {
                    if (Current(path) != null || (index && worktree && entries.ContainsKey(path)))
                    {
                        g.Err(SubMsg("apply", worktree ? "alreadyExists" : "alreadyInIndex", ("path", path)) + "\n");
                        throw new ApplyFailed();
                    }
                    (oldMode, oldData) = (null, []);
                }
                else
                {
                    if (Current(fp.Old) is not { } have)
                    {
                        g.Err(SubMsg("apply", worktree ? "noSuchFile" : "notInIndex", ("path", fp.Old)) + "\n");
                        throw new ApplyFailed();
                    }
                    if (index && worktree && !state.ContainsKey(fp.Old) && !(entries.TryGetValue(fp.Old, out var e) && ObjectId("blob", have.Data) == e.Sha))
                    {
                        g.Err(SubMsg("apply", "notMatchIndex", ("path", fp.Old)) + "\n");
                        throw new ApplyFailed();
                    }
                    (oldMode, oldData) = have;
                }
            }
            catch (ApplyFailed)
            {
                ok = false;
                continue;
            }
            byte[] newData;
            try { newData = ApplyHunks(g, oldData, fp, fp.Old ?? path, verbose); }
            catch (ApplyFailed)
            {
                g.Err(SubMsg("apply", "doesNotApply", ("path", fp.Old ?? path)) + "\n");
                ok = false;
                continue;
            }
            if (fp.Old != null && fp.Old != fp.New) state[fp.Old] = null;
            if (fp.New != null) state[fp.New] = (fp.NewMode ?? oldMode ?? "100644", newData);
        }
        if (!ok || check) return ok;
        foreach (var fp in patches)
            if (verbose) g.Err(SubMsg("apply", "appliedCleanly", ("path", fp.New ?? fp.Old ?? "")) + "\n");
        foreach (var (p, v) in state)
        {
            g.Check();
            if (worktree)
            {
                var full = top.TrimEnd('/') + "/" + p;
                if (v is not { } put)
                {
                    if (repo != null) repo.WtRemove(p);
                    else if (fs.Exists(full, false)) fs.Delete(full);
                }
                else
                {
                    fs.CreateDirectory(VirtualFileSystem.GetParent(full), parents: true);
                    fs.WriteAllBytes(full, put.Data);
                    fs.Chmod(full, (put.Mode == "100755" ? 0x1FF : 0x1B6) & ~fs.Umask);
                }
            }
            if (index)
            {
                if (v is not { } put) entries.Remove(p);
                else
                {
                    var sha = repo!.Put("blob", put.Data);
                    entries[p] = worktree ? repo.EntryFor(p, put.Mode, sha) : new Entry(p, sha, put.Mode);
                }
            }
        }
        if (index) repo!.SaveIndex(entries.Values.OrderBy(e => e.Path, ByBytes));
        return true;
    }

    static void ApplyStat(Cmd g, List<FilePatch> patches, bool numstat, bool stat, bool summary)
    {
        static string Label(FilePatch fp) => fp.Old != null && fp.New != null && fp.Old != fp.New ? $"{fp.Old} => {fp.New}" : CQuote(fp.New ?? fp.Old ?? "");
        if (numstat)
            foreach (var fp in patches)
            {
                var (a, d) = fp.Counts();
                g.Out((fp.Binary ? "-\t-\t" : $"{a}\t{d}\t") + Label(fp) + "\n");
            }
        if (stat)
        {
            var names = patches.Select(Label).ToList();
            int width = Math.Min(names.Count > 0 ? names.Max(x => x.Length) : 0, 50);
            int room = 70 - width;
            int topChange = patches.Count > 0 ? patches.Max(fp => fp.Counts().Ins + fp.Counts().Dels) : 0;
            for (int k = 0; k < patches.Count; k++)
            {
                var (a, d) = patches[k].Counts();
                int total = a + d;
                if (topChange > room) (a, d) = ((a * room + topChange - 1) / topChange, (d * room + topChange - 1) / topChange);  // rounded up: a change never vanishes
                g.Out($" {names[k].PadRight(width)} |{total,5} {new string('+', a)}{new string('-', d)}\n");
            }
            int ins = patches.Sum(fp => fp.Counts().Ins), dels = patches.Sum(fp => fp.Counts().Dels), files = patches.Count;
            g.Out($" {files} file{(files != 1 ? "s" : "")} changed, {ins} insertion{(ins != 1 ? "s" : "")}(+), {dels} deletion{(dels != 1 ? "s" : "")}(-)\n");
        }
        if (summary)
            foreach (var fp in patches)
            {
                if (fp.Old == null) g.Out($" create mode {fp.NewMode ?? "100644"} {CQuote(fp.New!)}\n");
                else if (fp.New == null) g.Out($" delete mode {fp.OldMode ?? "100644"} {CQuote(fp.Old)}\n");
                else
                {
                    if (fp.Old != fp.New) g.Out($" rename {RenameLabel(fp.Old, fp.New)} ({fp.Similarity}%)\n");
                    if (fp.OldMode != null && fp.NewMode != null && fp.OldMode != fp.NewMode) g.Out($" mode change {fp.OldMode} => {fp.NewMode} {CQuote(fp.New)}\n");
                }
            }
    }

    static int GitApply(Cmd g, List<string> args)
    {
        var o = Parse("apply", args);
        var repo = g.FindRepo();
        if (repo == null && (o.Has("\x05") || o.Has("\x06"))) repo = g.Repo();  // the index needs a repository: fails with git's message
        var top = repo != null ? repo.Top : g.Cwd;
        var pv = o.Val("p") ?? "";
        int strip = pv.Length > 0 && pv.All(char.IsAsciiDigit) && int.TryParse(pv, out var sp) ? sp : 1;
        var patches = new List<FilePatch>();
        foreach (var f in o.Args.Count > 0 ? o.Args : ["-"])
        {
            byte[] data;
            if (f == "-") data = ByteText.Encode(g.Ctx.StdinText);
            else
            {
                var path = VirtualFileSystem.Normalize(g.Cwd, f);
                if (!g.Ctx.Fs.IsFile(path))
                    throw new GitException(SubMsg("apply", "cannotOpen", ("file", f), ("errno", g.Ctx.Fs.IsDirectory(path) ? "Is a directory" : "No such file or directory")), 128);
                data = g.Ctx.Fs.ReadAllBytes(path);
            }
            patches.AddRange(ParsePatch(data, strip));
        }
        if (patches.Count == 0) throw SubFatal("apply", "noPatches");
        if (repo != null && g.Cwd != repo.Top)
        {
            // from a subdirectory, files outside it are left alone, as git does
            var here = g.Cwd[(repo.Top.TrimEnd('/').Length + 1)..] + "/";
            patches = patches.Where(fp => (fp.New ?? fp.Old ?? "").StartsWith(here, StringComparison.Ordinal)).ToList();
        }
        if (o.Has("R")) foreach (var fp in patches) fp.Reverse();
        bool informative = o.Has("\x01") || o.Has("\x02") || o.Has("\x03");
        ApplyStat(g, patches, o.Has("\x02"), o.Has("\x01"), o.Has("\x03"));
        if (informative && !o.Has("\x07")) return 0;  // --stat and friends only describe the patch, even with --check
        bool cached = o.Has("\x06");
        return ApplyPatches(g, repo, top, patches, index: cached || o.Has("\x05"), worktree: !cached, verbose: o.Has("v"), check: o.Has("\x04")) ? 0 : 1;
    }

    // ---------- am ----------

    /// <summary>(name, email, date, subject, body, patch) from one message as git format-patch writes it.</summary>
    static (string Name, string Email, string? Date, string Subject, string Body, byte[] Patch) ParseMail(string text)
    {
        int cut = text.IndexOf("\n\n", StringComparison.Ordinal);
        string head = cut >= 0 ? text[..cut] : text, rest = cut >= 0 ? text[(cut + 2)..] : "";
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        string? last = null;
        foreach (var line in head.Split('\n'))
        {
            if (line.Length > 0 && line[0] is ' ' or '\t' && last != null) headers[last] += " " + line.Trim();
            else if (line.Contains(':') && !line.StartsWith("From ", StringComparison.Ordinal))
            {
                int colon = line.IndexOf(':');
                last = line[..colon].Trim().ToLowerInvariant();
                headers[last] = line[(colon + 1)..].Trim();
            }
        }
        var from = headers.GetValueOrDefault("from", "");
        var m = Regex.Match(from, @"\A\s*(.*?)\s*<([^<>]*)>\s*\z", RegexOptions.Singleline);
        var (name, email) = m.Success ? (m.Groups[1].Value.Trim('"'), m.Groups[2].Value) : (from, from);
        var subject = headers.GetValueOrDefault("subject", "");
        subject = Regex.Replace(subject, @"^(\s*(\[[^\]]*\]|re:)\s*)+", "", RegexOptions.IgnoreCase);
        subject = Regex.Replace(subject, @"\s+", " ").Trim();
        var bodyLines = new List<string>();
        int? patchAt = null;
        var lines = rest.Split('\n');
        for (int k = 0; k < lines.Length; k++)
        {
            var line = lines[k];
            if (line == "---" || line.StartsWith("diff --git ", StringComparison.Ordinal) || line.StartsWith("Index: ", StringComparison.Ordinal)
                || (line.StartsWith("--- ", StringComparison.Ordinal) && k + 1 < lines.Length && lines[k + 1].StartsWith("+++ ", StringComparison.Ordinal)))
            {
                patchAt = k;
                break;
            }
            bodyLines.Add(line);
        }
        var body = string.Join("\n", bodyLines).Trim('\n');
        var patch = patchAt is { } at ? string.Join("\n", lines[at..]) : "";
        return (name, email, headers.GetValueOrDefault("date"), subject, body, ByteText.Encode(patch));
    }

    static readonly Regex MboxFrom = new(@"\AFrom \S+ \w{3} \w{3} [ \d]\d \d\d:\d\d:\d\d \d{4}");

    /// <summary>The messages in a mailbox: each starts at a line beginning 'From '; a file without one is a single message.</summary>
    static List<string> SplitMbox(string text)
    {
        if (!text.StartsWith("From ", StringComparison.Ordinal)) return Regex.IsMatch(text, "^(From|Subject):", RegexOptions.Multiline) ? [text] : [];
        var output = new List<string>();
        List<string>? cur = null;
        foreach (var line in text.Split('\n'))
        {
            if (line.StartsWith("From ", StringComparison.Ordinal) && MboxFrom.IsMatch(line))
            {
                if (cur != null) output.Add(string.Join("\n", cur));
                cur = [];
                continue;
            }
            cur?.Add(line);
        }
        if (cur != null) output.Add(string.Join("\n", cur));
        return output;
    }

    static int GitAm(Cmd g, List<string> args)
    {
        string? show = null;
        var rest = new List<string>();
        foreach (var a in args)
        {
            if (Regex.IsMatch(a, @"\A--show-current-patch(=.*)?\z", RegexOptions.Singleline))
            {
                int eq = a.IndexOf('=');
                show = eq >= 0 && eq + 1 < a.Length ? a[(eq + 1)..] : "raw";
            }
            else rest.Add(a);
        }
        var o = Parse("am", rest);
        var repo = g.Repo();
        const string st = "rebase-apply";
        bool busy = repo.GExists($"{st}/next");
        string Mail(int k) => ByteText.Decode(repo.GRead($"{st}/{k:D4}") ?? []);
        int Number() => int.TryParse((repo.GText($"{st}/next") ?? "1").Trim(), out var v) ? v : 1;
        void Finish() => repo.Fs.Delete(repo.GPath(st), recursive: true);
        if (o.Has("\x03") || o.Has("\x02") || o.Has("\x01") || show != null)
        {
            if (!busy) throw SubFatal("am", "notInProgress");
            if (show != null)
            {
                var text = Mail(Number());
                g.Out(show == "diff" ? ByteText.Decode(ParseMail(text).Patch) : text);
                return 0;
            }
            var head = repo.Head();
            if (o.Has("\x03"))
            {
                var orig = (repo.GText("ORIG_HEAD") ?? "").Trim() is { Length: > 0 } oh ? oh : head;
                if ((repo.GText($"{st}/abort-safety") ?? "").Trim() != (head ?? ""))
                    g.Err(SubMsg("am", "movedHead") + "\n");  // HEAD moved since the failure: only the session is dropped
                else
                {
                    SwitchTrees(g, repo, head, orig, force: true);
                    if (orig != null && orig != head) repo.UpdateRef("HEAD", orig, "am --abort");
                }
                Finish();
                return 0;
            }
            if (o.Has("\x02"))
            {
                SwitchTrees(g, repo, head, head, force: true);
                repo.GWrite($"{st}/next", $"{Number() + 1}\n");
            }
            else
            {
                var (name, email, date, subject, body, _) = ParseMail(Mail(Number()));
                var tree = repo.WriteTree(IndexFiles(repo.Index()));
                if (tree == (head != null ? repo.Commit(head).Tree : EmptyTree))
                {
                    g.Out(SubMsg("am", "applying", ("subject", subject)) + "\n");
                    g.Out(SubMsg("am", "noChanges") + "\n" + SubMsg("am", "resolveHint") + "\n");
                    return 128;
                }
                if (!o.Has("q")) g.Out(SubMsg("am", "applying", ("subject", subject)) + "\n");
                AmCommit(g, repo, tree, name, email, date, subject, body);
                repo.GWrite($"{st}/next", $"{Number() + 1}\n");
            }
        }
        else
        {
            if (busy) throw SubFatal("am", "inProgress");
            var texts = new List<string>();
            foreach (var f in o.Args.Count > 0 ? o.Args : ["-"])
            {
                if (f == "-") { texts.Add(g.Ctx.StdinText); continue; }
                var path = VirtualFileSystem.Normalize(g.Cwd, f);
                if (!g.Ctx.Fs.IsFile(path)) throw SubFatal("am", "cannotOpen", ("file", f), ("errno", "No such file or directory"));
                texts.Add(ByteText.Decode(g.Ctx.Fs.ReadAllBytes(path)));
            }
            var mails = texts.SelectMany(SplitMbox).ToList();
            if (mails.Count == 0) throw new GitException(SubMsg("am", "notPatch"), 128);
            var head = repo.Head();
            for (int k = 1; k <= mails.Count; k++) repo.GWrite($"{st}/{k:D4}", mails[k - 1]);
            repo.GWrite($"{st}/last", $"{mails.Count}\n");
            repo.GWrite($"{st}/next", "1\n");
            repo.GWrite($"{st}/abort-safety", (head ?? "") + "\n");
            if (head != null) repo.GWrite("ORIG_HEAD", head + "\n");
            var staged = DiffFiles(TreeFiles(repo, head), IndexFiles(repo.Index()));
            if (staged.Count > 0)  // like git, the session is already set up and stays behind
                throw SubFatal("am", "dirtyIndex", ("paths", string.Join(", ", staged.Select(c => c.Path))));
        }
        int lastMail = int.TryParse((repo.GText($"{st}/last") ?? "0").Trim(), out var lv) ? lv : 0;
        while (Number() <= lastMail)
        {
            int k = Number();
            var (name, email, date, subject, body, patch) = ParseMail(Mail(k));
            if (!o.Has("q")) g.Out(SubMsg("am", "applying", ("subject", subject)) + "\n");
            List<FilePatch>? patches;
            try { patches = ParsePatch(patch, 1); }
            catch (GitException e)
            {
                g.Err(e.Message + "\n");
                patches = null;
            }
            if (patches is { Count: 0 })
            {
                repo.GWrite($"{st}/abort-safety", (repo.Head() ?? "") + "\n");
                g.Out(SubMsg("am", "emptyPatch") + "\n" + SubMsg("am", "resolveHint") + "\n");
                return 128;
            }
            if (patches == null || !ApplyPatches(g, repo, repo.Top, patches, index: true, worktree: true, verbose: false, check: false))
            {
                g.Err(SubMsg("am", "showHint") + "\n");
                repo.GWrite($"{st}/abort-safety", (repo.Head() ?? "") + "\n");
                g.Out(SubMsg("am", "patchFailedAt", ("number", k.ToString("D4")), ("subject", subject)) + "\n" + SubMsg("am", "resolveHint") + "\n");
                return 128;
            }
            var tree = repo.WriteTree(IndexFiles(repo.Index()));
            AmCommit(g, repo, tree, name, email, date, subject, body);
            repo.GWrite($"{st}/next", $"{k + 1}\n");
        }
        Finish();
        return 0;
    }

    static void AmCommit(Cmd g, Repo repo, string tree, string name, string email, string? date, string subject, string body)
    {
        var head = repo.Head();
        var author = g.Ident("author", name: name, email: email, date: date);
        var committer = g.Ident("committer");
        var message = subject + (body.Length > 0 ? "\n\n" + body : "") + "\n";
        var data = $"tree {tree}\n" + (head != null ? $"parent {head}\n" : "") + $"author {author}\ncommitter {committer}\n\n{message}";
        var sha = repo.Put("commit", data);
        repo.UpdateRef("HEAD", sha, $"am: {subject}");
    }

    // ---------- format-patch ----------

    static int GitFormatPatch(Cmd g, List<string> args)
    {
        var o = Parse("format-patch", NumericShorthand(args));
        var repo = g.Repo();
        var nv = o.Val("n") ?? "";
        int? limit = nv.Length > 0 && nv.All(char.IsAsciiDigit) ? (int.TryParse(nv, out var lim) ? lim : int.MaxValue) : null;
        List<string> include = [], exclude = [];
        foreach (var a in o.Args)
        {
            int dots = a.IndexOf("..", StringComparison.Ordinal);
            if (dots >= 0)
            {
                string left = a[..dots], right = a[(dots + 2)..];
                exclude.Add(CommitOf(repo, left.Length > 0 ? left : "HEAD"));
                include.Add(CommitOf(repo, right.Length > 0 ? right : "HEAD"));
            }
            else if (limit != null) include.Add(CommitOf(repo, a));
            else exclude.Add(CommitOf(repo, a));  // <since>: the commits after it, up to HEAD
        }
        if (include.Count == 0) include = [repo.Head() ?? throw Fatal("badDefaultRevision")];
        var excluded = exclude.Count > 0 ? Ancestors(repo, exclude) : [];
        var order = WalkOrder(repo, include).Where(c => !excluded.Contains(c) && repo.Commit(c).Parents.Count < 2).ToList();
        if (limit is { } l) order = order.Take(l).ToList();
        order.Reverse();
        int total = order.Count;
        var outDir = o.Val("o");
        Opts statOpts = new(), patchOpts = new();
        statOpts.Flags["\x01"] = 1;
        statOpts.Flags["\x1f"] = 1;
        for (int k = 1; k <= total; k++)
        {
            g.Check();
            var sha = order[k - 1];
            var c = repo.Commit(sha);
            var who = ParseIdent(c.Author);
            var lines = c.Message.TrimEnd('\n').Split('\n');
            int subjectEnd = Array.FindIndex(lines, x => x.Trim().Length == 0);
            if (subjectEnd < 0) subjectEnd = lines.Length;
            var subject = string.Join(" ", lines[..subjectEnd].Select(x => x.Trim()));
            var body = string.Join("\n", lines[subjectEnd..]).Trim('\n');
            FileMap a = TreeFiles(repo, c.Parents.Count > 0 ? c.Parents[0] : null), b = repo.Flatten(c.Tree);
            StringBuilder stat = new(), patch = new();
            WriteDiff(g, repo, a, b, statOpts, output: x => stat.Append(x));  // shortcut: the stat is drawn 80 columns wide, real git uses 72
            WriteDiff(g, repo, a, b, patchOpts, output: x => patch.Append(x));
            var prefix = total > 1 ? $"[PATCH {k}/{total}]" : "[PATCH]";
            var text = $"From {sha} Mon Sep 17 00:00:00 2001\nFrom: {who.Name} <{who.Email}>\nDate: {FormatDate(who.Epoch, who.Zone, "rfc", g.Now)}\n"
                + $"Subject: {prefix} {subject}\n\n" + (body.Length > 0 ? body + "\n" : "") + "---\n" + stat + "\n"
                + patch + "-- \n" + Spec.Version.Split(' ')[^1] + "\n\n";
            if (o.Has("\x01"))
            {
                g.Out(text);
                continue;
            }
            var slug = Regex.Replace(subject, "[^A-Za-z0-9._]+", "-").Trim('-', '.');
            slug = (slug.Length > 52 ? slug[..52] : slug).TrimEnd('-', '.');
            var name = $"{k:D4}-{slug}.patch";
            var shown = outDir != null ? outDir.TrimEnd('/') + "/" + name : name;
            var target = VirtualFileSystem.Normalize(g.Cwd, shown);
            g.Ctx.Fs.CreateDirectory(VirtualFileSystem.GetParent(target), parents: true);
            g.Ctx.Fs.WriteAllBytes(target, ByteText.Encode(text));
            g.Out(shown + "\n");
        }
        return 0;
    }

    // ---------- bisect ----------

    static List<string> BisectMarks(Repo repo, string kind) =>
        repo.Refs("refs/bisect/").Where(r => r.Name.StartsWith($"refs/bisect/{kind}-", StringComparison.Ordinal)).Select(r => r.Sha).ToList();

    static void BisectLog(Repo repo, string text) => repo.GWrite("BISECT_LOG", [.. repo.GRead("BISECT_LOG") ?? [], .. ByteText.Encode(text)]);

    static void BisectMark(Repo repo, string kind, string sha)
    {
        var c = repo.Commit(sha);
        repo.UpdateRef(kind == "bad" ? "refs/bisect/bad" : $"refs/bisect/{kind}-{sha}", sha);
        BisectLog(repo, $"# {kind}: [{sha}] {c.Subject}\ngit bisect {kind} {sha}\n");
    }

    /// <summary>About how many more steps n candidates take: log2, rounded down when n is close to a power of two.</summary>
    static int BisectSteps(int n)
    {
        if (n < 3) return 0;
        int p = BitOperations.Log2((uint)n);
        return (1 << p) < 3 * (n - (1 << p)) ? p : p - 1;
    }

    /// <summary>Checks out the next commit to test, or reports the first bad one. Returns "wait", "next", "found" or "skipped".</summary>
    static string BisectNext(Cmd g, Repo repo)
    {
        var bad = repo.ReadRef("refs/bisect/bad");
        var goods = BisectMarks(repo, "good");
        if (bad == null || goods.Count == 0)
        {
            var line = bad == null && goods.Count == 0 ? SubMsg("bisect", "statusBoth") : bad != null ? SubMsg("bisect", "statusGood")
                : SubMsg("bisect", goods.Count == 1 ? "statusBad" : "statusBadMany", ("count", goods.Count.ToString()));
            g.Out(line + "\n");
            BisectLog(repo, $"# {line}\n");
            return "wait";
        }
        var candidates = Ancestors(repo, [bad]);
        candidates.ExceptWith(Ancestors(repo, goods));
        if (candidates.Count == 0) throw new GitException(SubMsg("bisect", "bothGoodAndBad", ("commit", bad)), 1);
        var skipped = BisectMarks(repo, "skip").ToHashSet(StringComparer.Ordinal);
        var order = WalkOrder(repo, [bad], candidates);
        order.Reverse();  // oldest first: of equally good splits, the older one is tested
        int total = candidates.Count;
        string? best = null;
        int bestScore = -1, reach = 0;
        if (total > 1)
        {
            // shortcut: counts reachable candidates per commit (quadratic); fine for the histories a machine holds
            foreach (var c in order)
            {
                if (skipped.Contains(c) || c == bad) continue;
                var mine = Ancestors(repo, [c]);
                mine.IntersectWith(candidates);
                int w = mine.Count;
                if (Math.Min(w, total - w) > bestScore) (best, bestScore, reach) = (c, Math.Min(w, total - w), w);
            }
        }
        if (best == null)
        {
            if (total > 1 || skipped.Contains(bad))
            {
                var left = order.AsEnumerable().Reverse().Where(c => skipped.Contains(c) && c != bad).Append(bad);
                g.Out(SubMsg("bisect", "onlySkipped", ("commits", string.Join("\n", left))) + "\n");
                return "skipped";
            }
            var bc = repo.Commit(bad);
            g.Out(SubMsg("bisect", "firstBad", ("commit", bad)) + "\n");
            GitShow(g, ["--stat", bad]);
            BisectLog(repo, SubMsg("bisect", "logFirstBad", ("commit", bad), ("subject", bc.Subject)) + "\n");
            return "found";
        }
        int remaining = total - reach - 1, steps = BisectSteps(total);
        MoveHead(g, repo, null, best, best, force: false, quiet: true);
        g.Out(SubMsg("bisect", "bisecting", ("revisions", $"{remaining} revision{(remaining != 1 ? "s" : "")}"),
            ("steps", $"{steps} step{(steps != 1 ? "s" : "")}")) + "\n");
        g.Out(SubMsg("bisect", "current", ("commit", best), ("subject", repo.Commit(best).Subject)) + "\n");
        return "next";
    }

    static void BisectClear(Repo repo)
    {
        foreach (var (name, _) in repo.Refs("refs/bisect/")) repo.DeleteRef(name);
        foreach (var f in new[] { "BISECT_START", "BISECT_LOG" }) repo.GRemove(f);
    }

    static async Task<int> GitBisect(Cmd g, List<string> args)
    {
        var repo = g.Repo();
        if (args.Count == 0) throw new GitException(SubMsg("bisect", "needCommand") + "\n\n" + SubSpec("bisect").Help.TrimEnd('\n'), 129);
        var (action, rest) = (args[0], args[1..]);
        if (action.StartsWith('-')) Parse("bisect", [action]);  // bisect has no options: the usual unknown-option error
        bool busy = repo.GExists("BISECT_START");
        string CommitArg(string rev)
        {
            var sha = TryResolve(repo, rev);
            return (sha != null ? Peel(repo, sha, "commit") : null) ?? throw new GitException(SubMsg("bisect", "badRev", ("rev", rev)), 1);
        }
        if (action == "start")
        {
            var revs = rest.Where(r => r != "--").Select(CommitArg).ToList();
            if (busy) BisectClear(repo);
            repo.GWrite("BISECT_START", (repo.Branch() ?? repo.Head() ?? "") + "\n");
            BisectLog(repo, "git bisect start" + string.Concat(rest.Where(r => r != "--").Select(r => $" '{r}'")) + "\n");
            if (revs.Count > 0) BisectMark(repo, "bad", revs[0]);
            foreach (var r in revs.Skip(1)) BisectMark(repo, "good", r);
            BisectNext(g, repo);
            return 0;
        }
        if (action is "good" or "bad" or "old" or "new" or "skip")
        {
            if (!busy)
            {
                g.Err(SubMsg("bisect", "needStart") + "\n");
                return 1;
            }
            var kind = action switch { "old" => "good", "new" => "bad", _ => action };
            var shas = rest.Select(CommitArg).ToList();
            foreach (var sha in shas.Count > 0 ? shas : [CommitArg("HEAD")]) BisectMark(repo, kind, sha);
            return BisectNext(g, repo) == "skipped" ? 2 : 0;
        }
        if (action == "reset")
        {
            if (!busy)
            {
                g.Out(SubMsg("bisect", "notBisecting") + "\n");
                return 0;
            }
            var start = (repo.GText("BISECT_START") ?? "").Trim();
            var target = rest.Count > 0 ? rest[0] : start;
            if (repo.ReadRef("refs/heads/" + target) is { } branchSha) MoveHead(g, repo, "refs/heads/" + target, branchSha, target, force: false, quiet: false);
            else if (target.Length > 0) MoveHead(g, repo, null, CommitArg(target), target, force: false, quiet: false);
            BisectClear(repo);
            return 0;
        }
        if (action == "log")
        {
            if (!busy) throw new GitException(SubMsg("bisect", "logNotBisecting"), 1);
            g.Out(ByteText.Decode(repo.GRead("BISECT_LOG") ?? []));
            return 0;
        }
        if (action == "run")
        {
            if (!busy)
            {
                g.Err(SubMsg("bisect", "needStart") + "\n");
                return 1;
            }
            if (rest.Count == 0) throw new GitException(SubMsg("bisect", "needCommand"), 1);
            if (repo.ReadRef("refs/bisect/bad") == null || BisectMarks(repo, "good").Count == 0)
            {
                BisectNext(g, repo);
                return 1;
            }
            var shown = string.Join(" ", rest.Select(a => "'" + a.Replace("'", "'\\''") + "'"));
            while (true)
            {
                g.Out(SubMsg("bisect", "running", ("command", shown)) + "\n");
                int code = await g.Ctx.Shell.InvokeAsync(rest, g.Ctx.Io, g.Ctx.CancellationToken);
                if (!g.Ctx.Fs.IsDirectory(g.Cwd)) g.Cwd = g.Ctx.Shell.Cwd;
                if (code < 0 || code >= 128)
                {
                    g.Err(SubMsg("bisect", "runFailed", ("code", code.ToString()), ("command", shown)) + "\n");
                    return 127;
                }
                BisectMark(repo, code == 125 ? "skip" : code == 0 ? "good" : "bad", repo.Head()!);
                var result = BisectNext(g, repo);
                if (result == "found")
                {
                    g.Out(SubMsg("bisect", "runFound") + "\n");
                    return 0;
                }
                if (result == "skipped")
                {
                    g.Err(SubMsg("bisect", "runCannotContinue") + "\n");
                    return 2;
                }
                if (result == "wait") return 1;
            }
        }
        g.Err(SubMsg("bisect", "notAvailable", ("action", action)) + "\n");
        return 1;
    }
}
