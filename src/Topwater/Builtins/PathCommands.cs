// Directories and path names: cd, pwd, pushd, popd, dirs, basename, dirname, realpath, mktemp. See docs/architecture.md.

namespace Topwater;

internal static partial class Builtins
{
    static void RegisterPathCommands(Shell shell)
    {
        shell.Register("cd", ctx =>
        {
            var flags = ctx.Args.TakeWhile(x => x is "-L" or "-P" or "-e" or "-@").ToList();
            var args = ctx.Args.Skip(flags.Count).ToList();
            if (args.Count > 0 && args[0] == "--") args.RemoveAt(0);
            if (args.Count > 1) throw new ShellException(CommandSpec.Message("cd", "tooManyArguments"));
            var target = args.Count == 0 ? ctx.Shell.Environment.GetValueOrDefault("HOME", "/")
                : args[0] == "-" ? ctx.Shell.Environment.GetValueOrDefault("OLDPWD", ctx.Shell.Cwd) : args[0];
            // like bash: the new directory is the old one's logical path plus the operand, ".." removing a name (-L, the
            // default); if that isn't a directory, or with -P, the physical path, every symbolic link resolved
            bool physical = flags.LastOrDefault(f => f is "-L" or "-P") == "-P";
            var abs = VirtualFileSystem.Normalize(ctx.Shell.Cwd, target);
            if (physical || !ctx.Fs.IsDirectory(abs)) abs = ctx.ResolvePath(target);
            if (!ctx.Fs.Exists(abs)) throw new ShellException(CommandSpec.Message("cd", "failed", ("dir", target), ("errno", ctx.Fs.WhyMissing(abs))));
            if (!ctx.Fs.IsDirectory(abs)) throw new ShellException(CommandSpec.Message("cd", "failed", ("dir", target), ("errno", "Not a directory")));
            if (!ctx.Fs.CanExecute(abs)) throw new ShellException(CommandSpec.Message("cd", "failed", ("dir", target), ("errno", "Permission denied")));
            if (physical) abs = ctx.Fs.RealPath(abs, out _) ?? abs;
            ctx.Shell.Environment["OLDPWD"] = ctx.Shell.Cwd;
            ctx.Shell.Cwd = abs;
            if (args is ["-"]) ctx.Stdout.WriteLine(abs);
            return 0;
        });

        shell.Register("pwd", ctx =>
        {
            // -P: the physical path, symbolic links resolved; -L (the default): the logical one cd kept
            bool physical = ctx.Args.LastOrDefault(x => x is "-L" or "-P") == "-P";
            ctx.Stdout.WriteLine(physical ? ctx.Fs.RealPath(ctx.Shell.Cwd, out _) ?? ctx.Shell.Cwd : ctx.Shell.Cwd);
            return 0;
        });

        string Dirs(Shell sh)
        {
            var home = sh.Environment.GetValueOrDefault("HOME", "");
            string Short(string d) => home.Length > 1 && (d == home || d.StartsWith(home + "/")) ? "~" + d[home.Length..] : d;
            return string.Join(' ', new[] { sh.Cwd }.Concat(sh.DirStack).Select(Short));
        }
        int ChangeDir(CommandContext ctx, string dir)
        {
            var abs = ctx.ResolvePath(dir);
            if (!ctx.Fs.IsDirectory(abs)) throw new ShellException(CommandSpec.Message("pushd", "noDirectory", ("dir", dir)));
            ctx.Shell.Environment["OLDPWD"] = ctx.Shell.Cwd;
            ctx.Shell.Cwd = abs;
            return 0;
        }
        shell.Register("pushd", ctx =>
        {
            var sh = ctx.Shell;
            var args = ctx.Args.Where(a => a != "-n").ToList();
            var old = sh.Cwd;
            if (args.Count == 0)
            {
                if (sh.DirStack.Count == 0) throw new ShellException(CommandSpec.Message("pushd", "noOtherDirectory"));
                ChangeDir(ctx, sh.DirStack[0]);
                sh.DirStack[0] = old;
            }
            else { ChangeDir(ctx, args[0]); sh.DirStack.Insert(0, old); }
            ctx.Stdout.WriteLine(Dirs(sh));
            return 0;
        });
        shell.Register("popd", ctx =>
        {
            var sh = ctx.Shell;
            if (sh.DirStack.Count == 0) throw new ShellException(CommandSpec.Message("popd", "stackEmpty"));
            ChangeDir(ctx, sh.DirStack[0]);
            sh.DirStack.RemoveAt(0);
            ctx.Stdout.WriteLine(Dirs(sh));
            return 0;
        });
        shell.Register("dirs", ctx =>
        {
            if (ctx.Args.Contains("-c")) { ctx.Shell.DirStack.Clear(); return 0; }
            var line = Dirs(ctx.Shell);
            if (ctx.Args.Any(a => a is "-p" or "-v"))
            {
                var all = line.Split(' ');
                for (int i = 0; i < all.Length; i++) ctx.Stdout.WriteLine(ctx.Args.Contains("-v") ? $"{i,2}  {all[i]}" : all[i]);
            }
            else ctx.Stdout.WriteLine(line);
            return 0;
        });

        shell.Register("basename", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("basename"));
            if (a.Positional.Count == 0) throw new UsageException(CommandSpec.Message("basename", "missingOperand"));
            var suffix = a.Value('s') ?? (!a.Has('a') && a.Positional.Count == 2 ? a.Positional[1] : null);
            var names = a.Has('a') || a.Value('s') != null ? a.Positional : a.Positional.Take(1).ToList();
            foreach (var n in names)
            {
                var b = n.TrimEnd('/') is var t && t.Length == 0 ? "/" : t[(t.LastIndexOf('/') + 1)..];
                if (suffix != null && b.EndsWith(suffix) && b != suffix) b = b[..^suffix.Length];
                ctx.Stdout.WriteLine(b);
            }
            return 0;
        });

        shell.Register("dirname", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("dirname"));
            if (a.Positional.Count == 0) throw new UsageException(CommandSpec.Message("dirname", "missingOperand"));
            foreach (var n in a.Positional)
            {
                var t = n.TrimEnd('/');
                int i = t.LastIndexOf('/');
                ctx.Stdout.Write((t.Length == 0 ? "/" : i < 0 ? "." : i == 0 ? "/" : t[..i].TrimEnd('/') is var d && d.Length > 0 ? d : "/") + (a.Has('z') ? "\0" : "\n"));
            }
            return 0;
        });

        CommandHandler realpath = ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of(ctx.Name));
            bool readlink = ctx.Name == "readlink", plain = readlink && !a.Has('f') && !a.Has('e') && !a.Has('m');
            // readlink is silent about failures unless -v; realpath reports them unless -q
            bool report = readlink ? a.Has('v') : !a.Has('q');
            if (a.Positional.Count == 0 && readlink) throw new UsageException(CommandSpec.Message("readlink", "missingOperand"));
            var fs = ctx.Fs;
            var end = a.Has('z') ? "\0" : readlink && a.Has('n') && a.Positional.Count == 1 ? "" : "\n";
            int code = 0;
            foreach (var p in a.Positional.DefaultIfEmpty("."))
            {
                var abs = ctx.ResolvePath(p);
                string? result, why;
                if (plain)
                    try { result = fs.ReadLink(abs); why = null; }
                    catch (ShellException e) { result = null; why = Reason(e); }
                else if (!readlink && a.Has('s')) { result = VirtualFileSystem.Normalize(ctx.Shell.Cwd, p); why = null; } // -s: no links resolved
                else result = fs.RealPath(abs, out why, existing: a.Has('e')) ?? (a.Has('m') ? abs : null);
                if (result == null) { if (report) ctx.Stderr.WriteLine($"{ctx.Name}: " + CommandSpec.Message(ctx.Name, "failed", ("file", p), ("errno", why ?? ""))); code = 1; continue; }
                ctx.Stdout.Write(result + end);
            }
            return Task.FromResult(code);
        };
        shell.Register("realpath", realpath);
        shell.Register("readlink", realpath);

        shell.Register("mktemp", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("mktemp"));
            var template = a.Positional.FirstOrDefault() ?? "tmp.XXXXXXXXXX";
            const string chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
            var name = System.Text.RegularExpressions.Regex.Replace(template, "X{3,}$", m => new string(Enumerable.Range(0, m.Length).Select(_ => chars[ctx.Shell.NextRandom(chars.Length)]).ToArray()));
            var path = template.Contains('/') && a.Value('p') == null && !a.Has('t') ? ctx.ResolvePath(name) : VirtualFileSystem.Normalize(a.Value('p') ?? "/tmp", name);
            if (a.Has('d')) ctx.Fs.CreateDirectory(path); else ctx.Fs.WriteAllText(path, "");
            ctx.Stdout.WriteLine(path);
            return 0;
        });
    }
}
