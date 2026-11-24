using System.Text;

namespace Topwater;

internal static partial class Builtins
{
    static string NotFound(string p) => $"cannot access '{p}': No such file or directory";

    static string HumanSize(long n) => n < 1024 ? n.ToString() : n < 1024 * 1024 ? $"{n / 1024.0:0.#}K" : $"{n / (1024.0 * 1024):0.#}M";

    static string LongEntry(CommandContext ctx, string abs, string name, bool human)
    {
        var st = ctx.Fs.Stat(abs);
        var size = human ? HumanSize(st.Size) : st.Size.ToString();
        return $"{VirtualFileSystem.FormatMode(st.IsDirectory, st.Mode)} 1 {st.Owner,-5} {st.Owner,-5} {size,6} {st.Modified:MMM dd HH:mm} {name}";
    }

    static void RegisterFileCommands(Shell shell)
    {
        shell.Register("ls", ctx =>
        {
            var a = Args.Parse(ctx.Args, "alhA1dFrtRS", longs: "all=a,almost-all=A,human-readable=h,directory=d,reverse=r,recursive=R");
            var paths = a.Positional.Count > 0 ? a.Positional : ["."];
            bool lng = a.Has('l'), human = a.Has('h');
            int code = 0;
            string Show(string abs, string name) =>
                (lng ? LongEntry(ctx, abs, name, human) : name) + (a.Has('F') && ctx.Fs.IsDirectory(abs) && name is not "." and not ".." ? "/" : "");

            var files = paths.Where(p => ctx.Fs.Exists(ctx.ResolvePath(p)) && (a.Has('d') || !ctx.Fs.IsDirectory(ctx.ResolvePath(p)))).ToList();
            foreach (var p in paths.Where(p => !ctx.Fs.Exists(ctx.ResolvePath(p)))) { ctx.Stderr.WriteLine($"ls: {NotFound(p)}"); code = 2; }
            foreach (var f in files) ctx.Stdout.WriteLine(Show(ctx.ResolvePath(f), f));

            var dirs = paths.Except(files).Where(p => ctx.Fs.IsDirectory(ctx.ResolvePath(p))).ToList();
            bool first = files.Count == 0;
            void ListDir(string abs, string label)
            {
                if (paths.Count > 1 || a.Has('R')) ctx.Stdout.WriteLine((first ? "" : "\n") + label + ":");
                first = false;
                var entries = ctx.Fs.List(abs).Where(n => a.Has('a') || a.Has('A') || !n.StartsWith('.')).ToList();
                if (a.Has('t')) entries = entries.OrderByDescending(n => ctx.Fs.Stat(VirtualFileSystem.Normalize(abs, n)).Modified).ToList();
                if (a.Has('S')) entries = entries.OrderByDescending(n => ctx.Fs.Stat(VirtualFileSystem.Normalize(abs, n)).Size).ToList();
                if (a.Has('a')) entries.InsertRange(0, [".", ".."]);
                if (a.Has('r')) entries.Reverse();
                if (lng) ctx.Stdout.WriteLine($"total {entries.Count * 4}");
                foreach (var n in entries) ctx.Stdout.WriteLine(Show(VirtualFileSystem.Normalize(abs, n), n));
                if (!a.Has('R')) return;
                foreach (var n in entries.Where(n => n is not "." and not ".."))
                {
                    var child = VirtualFileSystem.Normalize(abs, n);
                    if (ctx.Fs.IsDirectory(child)) ListDir(child, label.TrimEnd('/') + "/" + n);
                }
            }
            foreach (var d in dirs) ListDir(ctx.ResolvePath(d), d);
            return code;
        }, "List directory contents");

        shell.Register("cat", ctx =>
        {
            var a = Args.Parse(ctx.Args, "n", longs: "number=n");
            int code = 0, lineNo = 0;
            foreach (var (_, text) in Inputs(ctx, a.Positional, () => code = 1))
            {
                if (!a.Has('n')) { ctx.Stdout.Write(text); continue; }
                foreach (var line in SplitLines(text)) ctx.Stdout.Write($"{++lineNo,6}\t{line}\n");
            }
            return code;
        }, "Print files");

        shell.Register("mkdir", ctx =>
        {
            var a = Args.Parse(ctx.Args, "pv", longs: "parents=p");
            if (a.Positional.Count == 0) throw new ShellException("missing operand", 1);
            foreach (var p in a.Positional) ctx.Fs.CreateDirectory(ctx.ResolvePath(p), a.Has('p'));
            return 0;
        }, "Create directories");

        shell.Register("touch", ctx =>
        {
            if (ctx.Args.Count == 0) throw new ShellException("missing file operand");
            foreach (var p in ctx.Args.Where(p => !p.StartsWith('-'))) ctx.Fs.Touch(ctx.ResolvePath(p));
            return 0;
        }, "Create empty files / update timestamps");

        shell.Register("rm", ctx =>
        {
            var a = Args.Parse(ctx.Args, "rRfvd", longs: "recursive=r,force=f");
            bool recursive = a.Has('r') || a.Has('R');
            if (a.Positional.Count == 0 && !a.Has('f')) throw new ShellException("missing operand");
            int code = 0;
            foreach (var p in a.Positional)
            {
                var abs = ctx.ResolvePath(p);
                if (!ctx.Fs.Exists(abs)) { if (!a.Has('f')) { ctx.Stderr.WriteLine($"rm: cannot remove '{p}': No such file or directory"); code = 1; } continue; }
                if (ctx.Fs.IsDirectory(abs) && !recursive && !a.Has('d')) { ctx.Stderr.WriteLine($"rm: cannot remove '{p}': Is a directory"); code = 1; continue; }
                ctx.Fs.Delete(abs, recursive);
            }
            return code;
        }, "Remove files or directories");

        shell.Register("rmdir", ctx =>
        {
            foreach (var p in ctx.Args)
            {
                var abs = ctx.ResolvePath(p);
                if (!ctx.Fs.IsDirectory(abs)) throw new ShellException($"failed to remove '{p}': Not a directory");
                ctx.Fs.Delete(abs);
            }
            return 0;
        }, "Remove empty directories");

        CommandHandler copyOrMove = ctx =>
        {
            bool move = ctx.Name == "mv";
            var a = Args.Parse(ctx.Args, move ? "fvni" : "rRfvanp", longs: "recursive=r,force=f");
            if (a.Positional.Count < 2) throw new ShellException(a.Positional.Count == 0 ? "missing file operand" : $"missing destination file operand after '{a.Positional[0]}'");
            var sources = a.Positional.Take(a.Positional.Count - 1).ToList();
            var destArg = a.Positional[^1];
            var dest = ctx.ResolvePath(destArg);
            bool destIsDir = ctx.Fs.IsDirectory(dest);
            if (sources.Count > 1 && !destIsDir) throw new ShellException($"target '{destArg}' is not a directory");
            int code = 0;
            foreach (var s in sources)
            {
                var src = ctx.ResolvePath(s);
                if (!ctx.Fs.Exists(src)) { ctx.Stderr.WriteLine($"{ctx.Name}: cannot stat '{s}': No such file or directory"); code = 1; continue; }
                if (!move && ctx.Fs.IsDirectory(src) && !(a.Has('r') || a.Has('R') || a.Has('a')))
                { ctx.Stderr.WriteLine($"cp: -r not specified; omitting directory '{s}'"); code = 1; continue; }
                var target = destIsDir ? VirtualFileSystem.Normalize(dest, VirtualFileSystem.GetName(src)) : dest;
                if (target == src) { ctx.Stderr.WriteLine($"{ctx.Name}: '{s}' and '{destArg}' are the same file"); code = 1; continue; }
                if (move) ctx.Fs.Move(src, target); else ctx.Fs.Copy(src, target);
            }
            return Task.FromResult(code);
        };
        shell.Register("cp", copyOrMove, "Copy files and directories");
        shell.Register("mv", copyOrMove, "Move or rename files");

        shell.Register("find", Find.RunAsync, "Search for files");

        shell.Register("tree", ctx =>
        {
            var a = Args.Parse(ctx.Args, "ad", valued: "L");
            int maxDepth = a.Value('L') is { } l ? int.Parse(l) : int.MaxValue;
            var root = a.Positional.FirstOrDefault() ?? ".";
            var abs = ctx.ResolvePath(root);
            if (!ctx.Fs.IsDirectory(abs)) throw new ShellException($"{root} [error opening dir]");
            int dirs = 0, files = 0;
            var sb = new StringBuilder(root + "\n");
            void Walk(string dir, string prefix, int depth)
            {
                var entries = ctx.Fs.List(dir).Where(n => a.Has('a') || !n.StartsWith('.'))
                    .Where(n => !a.Has('d') || ctx.Fs.IsDirectory(VirtualFileSystem.Normalize(dir, n))).ToList();
                for (int i = 0; i < entries.Count; i++)
                {
                    bool last = i == entries.Count - 1;
                    var child = VirtualFileSystem.Normalize(dir, entries[i]);
                    sb.Append(prefix).Append(last ? "└── " : "├── ").Append(entries[i]).Append('\n');
                    if (ctx.Fs.IsDirectory(child))
                    {
                        dirs++;
                        if (depth < maxDepth) Walk(child, prefix + (last ? "    " : "│   "), depth + 1);
                    }
                    else files++;
                }
            }
            Walk(abs, "", 1);
            sb.Append($"\n{dirs} director{(dirs == 1 ? "y" : "ies")}").Append(a.Has('d') ? "\n" : $", {files} file{(files == 1 ? "" : "s")}\n");
            ctx.Stdout.Write(sb.ToString());
            return 0;
        }, "Show a directory tree");
    }
}
