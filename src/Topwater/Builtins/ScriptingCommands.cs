// Scripting builtins: echo, printf, test, bash, source, help, type, exit, set, trap, eval, command... See docs/architecture.md.

namespace Topwater;

internal static partial class Builtins
{
    /// <summary>Part of bash itself rather than a program in /usr/bin (for `type`, `command -v` and the "bash: " error prefix).</summary>
    internal static bool IsShellBuiltin(Shell shell, string name) => CommandSpec.Commands.TryGetValue(name, out var c) && c.ShellBuiltin && shell.IsBuiltin(name);

    static int ParseCount(CommandContext ctx, int fallback) =>
        ctx.Args.Count == 0 ? fallback : int.TryParse(ctx.Args[0], out var n) ? n : throw new ShellException(CommandSpec.Message(ctx.Name, "numericArgument", ("value", ctx.Args[0])), 2);

    static int LoopJump(CommandContext ctx, bool isBreak)
    {
        if (ctx.Shell.LoopDepth > 0) throw new LoopControl(isBreak, Math.Max(1, ParseCount(ctx, 1)));
        ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}{ctx.Name}: " + CommandSpec.Message(ctx.Name, "onlyInLoop"));
        return 0;
    }

    static readonly HashSet<string> ShellKeywords = ["if", "then", "else", "elif", "fi", "case", "esac", "for", "select", "while", "until", "do", "done", "in", "function", "time", "{", "}", "!", "[[", "]]", "coproc"];

    /// <summary>`type NAME` / `command -V NAME`: what the name runs, or false (with a message) when nothing.</summary>
    internal static bool Describe(CommandContext ctx, string a, bool all)
    {
        var sh = ctx.Shell;
        var lines = new List<string>();
        if (sh.Aliases.TryGetValue(a, out var v)) lines.Add($"{a} is aliased to `{v}'");
        if (ShellKeywords.Contains(a)) lines.Add($"{a} is a shell keyword");
        if (sh.Functions.TryGetValue(a, out var f)) lines.Add($"{a} is a function\n{a} () \n{f.Source}");
        if (IsShellBuiltin(sh, a)) lines.Add($"{a} is a shell builtin");
        if (all) lines.AddRange(sh.LocateAll(a).Select(p => $"{a} is {p}"));
        else if (lines.Count == 0 && sh.Locate(a) is { } l)
            lines.Add(!l.Hashed ? $"{a} is {l.Path}" : $"{a} is hashed ({(l.Path.StartsWith('/') || l.Path.StartsWith("./") ? "" : "./")}{l.Path})");
        if (lines.Count == 0) { ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}type: " + CommandSpec.Message("type", "notFound", ("name", a))); return false; }
        foreach (var line in all ? lines : lines.Take(1)) ctx.Stdout.WriteLine(line);
        return true;
    }

    static void RegisterScriptingCommands(Shell shell)
    {
        shell.Register("true", _ => 0);
        shell.Register(":", _ => 0);
        shell.Register("false", _ => 1);

        shell.Register("echo", ctx =>
        {
            var args = ctx.Args.ToList();
            bool newline = true, escapes = false;
            while (args.Count > 0 && args[0].Length > 1 && args[0][0] == '-' && args[0].Skip(1).All(c => c is 'n' or 'e' or 'E'))
            {
                if (args[0].Contains('n')) newline = false;
                if (args[0].Contains('e')) escapes = true;
                if (args[0].Contains('E')) escapes = false;
                args.RemoveAt(0);
            }
            var text = string.Join(' ', args);
            if (escapes && text.IndexOf("\\c", StringComparison.Ordinal) is var stop and >= 0) { text = text[..stop]; newline = false; } // \c: print nothing more
            ctx.Stdout.Write(escapes ? Unescape(text, keepUnknown: true, echo: true) : text);
            if (newline) ctx.Stdout.Write('\n');
            return 0;
        });

        shell.Register("printf", ctx =>
        {
            var args = ctx.Args.ToList();
            string? variable = null;
            if (args.Count >= 2 && args[0] == "-v") { variable = args[1]; args.RemoveRange(0, 2); }
            if (args.Count > 0 && args[0] == "--") args.RemoveAt(0);
            if (args.Count == 0) throw new ShellException(CommandSpec.Message("printf", "usage"), 2);
            var errors = new List<string>();
            var text = Printf.Format(args[0], args.Skip(1).ToList(), errors, ctx.Shell.Now);
            foreach (var e in errors) ctx.Stderr.WriteLine(ctx.Shell.ErrPrefix + e); // bash reports them as it goes, then writes the text
            if (variable == null) ctx.Stdout.Write(text);
            else ctx.Shell.SetVar(variable, text);
            return errors.Count > 0 ? 1 : 0;
        });

        shell.Register("history", ctx =>
        {
            var h = ctx.Shell.History;
            for (int i = 0; i < h.Count; i++) ctx.Stdout.WriteLine($"{i + 1,5}  {h[i]}");
            return 0;
        });

        shell.Register("which", ctx =>
        {
            int code = 0;
            bool all = ctx.Args.Contains("-a");
            foreach (var a in ctx.Args.Where(a => !a.StartsWith('-')))
            {
                var found = ctx.Shell.LocateAll(a);
                if (found.Count == 0) code = 1;
                foreach (var p in all ? found : found.Take(1)) ctx.Stdout.WriteLine(p);
            }
            return code;
        });

        shell.Register("type", ctx =>
        {
            int code = 0;
            var opts = Args.Parse(ctx.Args, CommandSpec.Of("type"));
            foreach (var a in opts.Positional)
            {
                if (opts.Has('t') || opts.Has('p') || opts.Has('P'))
                {
                    var kind = ctx.Shell.Aliases.ContainsKey(a) ? "alias" : ShellKeywords.Contains(a) ? "keyword" : ctx.Shell.Functions.ContainsKey(a) ? "function"
                        : IsShellBuiltin(ctx.Shell, a) ? "builtin" : null;
                    List<string> paths = kind != null && !opts.Has('P') ? [] : opts.Has('a') ? ctx.Shell.LocateAll(a) : ctx.Shell.Locate(a) is { } l ? [l.Path] : [];
                    kind ??= paths.Count > 0 ? "file" : null;
                    if (kind == null) { code = 1; continue; }
                    if (opts.Has('t')) ctx.Stdout.WriteLine(kind);
                    else foreach (var p in paths) ctx.Stdout.WriteLine(p);
                    continue;
                }
                if (!Describe(ctx, a, opts.Has('a'))) code = 1;
            }
            return code;
        });

        shell.Register("test", ctx => Test.Evaluate(ctx, ctx.Args.ToList()) ? 0 : 1);
        shell.Register("[", ctx =>
        {
            if (ctx.Args.Count == 0 || ctx.Args[^1] != "]") throw new ShellException(CommandSpec.Message("[", "missingBracket"), 2);
            return Test.Evaluate(ctx, ctx.Args.Take(ctx.Args.Count - 1).ToList()) ? 0 : 1;
        });

        CommandHandler runScript = async ctx =>
        {
            var io = ctx.Io;
            var args = ctx.Args.ToList();
            var sh = ctx.Shell;
            bool isBash = ctx.Name is "bash" or "sh", command = false, login = false;
            // bash's own options: -c, and -e -u -x -o pipefail ... which the new shell starts with
            var options = new List<(string Name, bool On)>();
            int i = 0;
            for (; isBash && i < args.Count && args[i].Length > 1 && args[i][0] is '-' or '+'; i++)
            {
                var f = args[i];
                if (f == "--") { i++; break; }
                if (f.StartsWith("--")) { login |= f == "--login"; continue; } // --norc, --noprofile
                foreach (var c in f[1..])
                {
                    if (c == 'c') command = true;
                    else if (c == 'l' && f[0] == '-') login = true;
                    else if (c == 'o' && i + 1 < args.Count) options.Add((args[++i], f[0] == '-'));
                    else if ("eufxC".Contains(c)) options.Add((c switch { 'e' => "errexit", 'u' => "nounset", 'f' => "noglob", 'x' => "xtrace", _ => "noclobber" }, f[0] == '-'));
                }
            }
            args = args[i..];
            void Setup() { foreach (var (name, on) in options) sh.SetOption(name, on); }
            if (command)
            {
                if (args.Count == 0) throw new ShellException(CommandSpec.Message(ctx.Name, "cOptionArgument"), 2);
                return await sh.RunScriptTextAsync(args[0], io, subshell: true, ctx.CancellationToken, args.Skip(2).ToList(), new(args.ElementAtOrDefault(1) ?? ctx.Name, null, Setup, login));
            }
            if (args.Count == 0)
            {
                if (!isBash) throw new ShellException(CommandSpec.Message(ctx.Name, "filenameRequired"), 2);
                return await sh.RunScriptTextAsync(ctx.StdinText, io with { Stdin = new StdinBuffer("") }, subshell: true, ctx.CancellationToken, [], new(ctx.Name, null, Setup, login));
            }
            string text;
            try { text = ctx.Fs.ReadAllText(ctx.ResolvePath(args[0])); }
            catch (ShellException e)
            {
                // a new bash reports its missing script itself, so without a line; source reports it at its own line
                ctx.Stderr.WriteLine($"{(isBash ? "bash: " : ctx.Shell.ErrPrefix)}{args[0]}: {e.Message[(e.Message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]}");
                return isBash ? 127 : 1;
            }
            if (isBash) return await sh.RunScriptTextAsync(text, io, subshell: true, ctx.CancellationToken, args.Skip(1).ToList(), new(args[0], args[0], Setup, login));
            sh.SourceDepth++;
            try { return await sh.RunScriptTextAsync(text, io, subshell: false, ctx.CancellationToken, args.Count > 1 ? args.Skip(1).ToList() : null, sourced: args[0]); }
            catch (ReturnSignal r) { return r.Code; }   // `return` in a sourced file ends that file only
            finally { sh.SourceDepth--; }
        };
        shell.Register("bash", runScript);
        shell.Register("sh", runScript);
        shell.Register("source", runScript);
        shell.Register(".", runScript);

        shell.Register("help", ctx =>
        {
            if (ctx.Args.Count > 0)
            {
                if (!ctx.Shell.Commands.TryGetValue(ctx.Args[0], out var c)) throw new ShellException(CommandSpec.Message("help", "noTopics", ("topic", ctx.Args[0])));
                ctx.Stdout.WriteLine(c.Usage ?? $"{c.Name}: {c.Description}");
                return 0;
            }
            var cmds = ctx.Shell.Commands.Values.OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            int width = cmds.Max(c => c.Name.Length) + 2;
            ctx.Stdout.WriteLine("Available commands (run '<command> --help' for details on non-standard ones):");
            foreach (var c in cmds) ctx.Stdout.WriteLine($"  {c.Name.PadRight(width)}{c.Description}");
            return 0;
        });
        shell.Register("exit", (Func<CommandContext, int>)(ctx =>
        {
            if (ctx.Args.Count > 0 && !int.TryParse(ctx.Args[0], out _))
            {
                // bash still exits, with status 2
                ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}exit: " + CommandSpec.Message("exit", "numericArgument", ("value", ctx.Args[0])));
                throw new ExitSignal(2);
            }
            throw new ExitSignal(ParseCount(ctx, ctx.Shell.LastExitCode) & 255);
        }));
        shell.Register("return", (Func<CommandContext, int>)(ctx =>
        {
            if (!ctx.Shell.InFunction && ctx.Shell.SourceDepth == 0) throw new ShellException(CommandSpec.Message("return", "notInFunction"), 2);
            throw new ReturnSignal(ParseCount(ctx, ctx.Shell.LastExitCode) & 255);
        }));
        shell.Register("break", (Func<CommandContext, int>)(ctx => LoopJump(ctx, true)));
        shell.Register("continue", (Func<CommandContext, int>)(ctx => LoopJump(ctx, false)));

        shell.Register("shift", ctx =>
        {
            int n = ParseCount(ctx, 1);
            var pos = ctx.Shell.Positional;
            if (n > pos.Count) return 1;
            ctx.Shell.Positional = pos.Skip(n).ToList();
            return 0;
        });

        shell.Register("trap", ctx =>
        {
            // EXIT runs when the tool call or script ends, ERR after a failing command, RETURN as a function returns;
            // other signals never arrive here
            var args = ctx.Args.Where(a => a != "--").ToList();
            var traps = ctx.Shell.Traps;
            static string Name(string sig)
            {
                var name = sig.ToUpperInvariant();
                if (name.StartsWith("SIG")) name = name[3..];
                return name == "0" ? "EXIT" : name;
            }
            if (args.Count == 0 || args[0] == "-p")
            {
                var only = args.Skip(1).Select(Name).ToHashSet();
                foreach (var (sig, action) in traps)
                    if (only.Count == 0 || only.Contains(sig)) ctx.Stdout.WriteLine($"trap -- {Shell.QuoteValue(action)} {sig}");
                return 0;
            }
            if (args[0] == "-l") { ctx.Stdout.WriteLine(" 1) SIGHUP\t 2) SIGINT\t 3) SIGQUIT\t 9) SIGKILL\t15) SIGTERM"); return 0; }
            var (act, signals) = args.Count == 1 ? (null, args) : (args[0], args.Skip(1).ToList());
            foreach (var sig in signals)
                if (act is null or "-") traps.Remove(Name(sig)); else traps[Name(sig)] = act;
            return 0;
        });

        shell.Register("shopt", ctx =>
        {
            string[] known = ["assoc_expand_once", "autocd", "cdable_vars", "cdspell", "checkhash", "checkjobs", "checkwinsize", "cmdhist",
                "complete_fullquote", "direxpand", "dirspell", "dotglob", "execfail", "expand_aliases", "extdebug", "extglob", "extquote",
                "failglob", "force_fignore", "globasciiranges", "globskipdots", "globstar", "gnu_errfmt", "histappend", "histreedit",
                "histverify", "hostcomplete", "huponexit", "inherit_errexit", "interactive_comments", "lastpipe", "lithist",
                "localvar_inherit", "localvar_unset", "login_shell", "mailwarn", "no_empty_cmd_completion", "nocaseglob", "nocasematch",
                "noexpand_translation", "nullglob", "patsub_replacement", "progcomp", "progcomp_alias", "promptvars", "restricted_shell",
                "shift_verbose", "sourcepath", "varredir_close", "xpg_echo"];
            var a = Args.Parse(ctx.Args, CommandSpec.Of("shopt"));
            if (a.Has('o'))
            {
                // shopt -o: the set -o options
                foreach (var n in a.Positional)
                    if (!ctx.Shell.SetOption(n, a.Has('s')) && (a.Has('s') || a.Has('u'))) throw new ShellException(CommandSpec.Message("shopt", "invalidOptionName", ("name", n)));
                if (a.Has('s') || a.Has('u')) return 0;
                foreach (var (n, on) in ctx.Shell.OptionList)
                    if (a.Positional.Count == 0 || a.Positional.Contains(n)) ctx.Stdout.WriteLine($"{n,-15}\t{(on ? "on" : "off")}");
                return 0;
            }
            var names = a.Positional.Count > 0 ? a.Positional.ToList() : known.ToList();
            if (a.Has('s') || a.Has('u'))
            {
                foreach (var n in names)
                {
                    if (!known.Contains(n)) throw new ShellException(CommandSpec.Message("shopt", "invalidShellOptionName", ("name", n)));
                    if (a.Has('s')) ctx.Shell.Shopts.Add(n); else ctx.Shell.Shopts.Remove(n);
                }
                return 0;
            }
            int code = 0;
            foreach (var n in names)
            {
                if (!known.Contains(n)) { ctx.Stderr.WriteLine("shopt: " + CommandSpec.Message("shopt", "invalidShellOptionName", ("name", n))); code = 1; continue; }
                bool on = ctx.Shell.Shopts.Contains(n);
                if (code == 0 && !on) code = 1;
                if (!a.Has('q')) ctx.Stdout.WriteLine($"{n,-15}\t{(on ? "on" : "off")}");
            }
            return code;
        });

        shell.Register("set", ctx =>
        {
            var args = ctx.Args.ToList();
            var sh = ctx.Shell;
            if (args.Count == 0)
            {
                foreach (var (k, v) in sh.Environment.OrderBy(e => e.Key, StringComparer.Ordinal)) ctx.Stdout.WriteLine($"{k}={v}");
                return 0;
            }
            int i = 0;
            for (; i < args.Count; i++)
            {
                var a = args[i];
                if (a == "--") { i++; sh.Positional = args.Skip(i).ToList(); return 0; }
                if (a == "-") { i++; break; }
                if (a.Length < 2 || a[0] is not ('-' or '+')) break;
                bool on = a[0] == '-';
                foreach (var c in a[1..])
                {
                    if (c == 'o')
                    {
                        if (i + 1 >= args.Count)
                        {
                            // set -o / set +o alone: list the options (as commands for +o)
                            foreach (var (n, value) in sh.OptionList)
                                ctx.Stdout.WriteLine(on ? $"{n,-15}\t{(value ? "on" : "off")}" : $"set {(value ? '-' : '+')}o {n}");
                            continue;
                        }
                        var opt = args[++i];
                        if (!sh.SetOption(opt, on)) throw new ShellException(CommandSpec.Message("set", "invalidOptionName", ("name", opt)), 2);
                    }
                    else if ("eufxC".Contains(c)) sh.SetOption(c switch { 'e' => "errexit", 'u' => "nounset", 'f' => "noglob", 'x' => "xtrace", _ => "noclobber" }, on);
                    else if (!"abhkmnpvBEHPT".Contains(c)) throw new ShellException(CommandSpec.Message("set", "builtinInvalidOption", ("option", $"{a[0]}{c}")), 2); // accepted, no effect here
                }
            }
            if (i < args.Count) sh.Positional = args.Skip(i).ToList();
            return 0;
        });

        shell.Register("getopts", ctx =>
        {
            if (ctx.Args.Count < 2) throw new ShellException(CommandSpec.Message("getopts", "usage"), 2);
            var sh = ctx.Shell;
            string spec = ctx.Args[0], name = ctx.Args[1];
            var args = ctx.Args.Count > 2 ? ctx.Args.Skip(2).ToList() : sh.Positional;
            bool silent = spec.StartsWith(':');
            int optind = int.TryParse(sh.GetVariable("OPTIND"), out var oi) && oi > 0 ? oi : 1;
            // where we are inside a group of options like -abc; restarts when the script resets OPTIND
            int pos = sh.GetoptsState.Optind == optind ? sh.GetoptsState.Pos : 1;
            int Done(int code, string result)
            {
                sh.SetVar(name, result);
                sh.GetoptsState = (optind, pos);
                sh.SetVar("OPTIND", optind.ToString());
                return code;
            }
            if (optind > args.Count) return Done(1, "?");
            var arg = args[optind - 1];
            if (pos == 1)
            {
                if (arg == "--") { optind++; return Done(1, "?"); }
                if (arg.Length < 2 || arg[0] != '-') return Done(1, "?");
            }
            char c = arg[pos];
            int at = c == ':' ? -1 : spec.IndexOf(c);
            if (++pos >= arg.Length) { optind++; pos = 1; }
            if (at < 0)
            {
                if (silent) sh.SetVar("OPTARG", c.ToString());
                else { ctx.Stderr.WriteLine(CommandSpec.Message("getopts", "illegalOption", ("script", sh.GetVariable("0")), ("option", c.ToString()))); sh.Unset("OPTARG"); }
                return Done(0, "?");
            }
            if (at + 1 >= spec.Length || spec[at + 1] != ':') { sh.Unset("OPTARG"); return Done(0, c.ToString()); }
            if (pos > 1) { sh.SetVar("OPTARG", arg[pos..]); optind++; pos = 1; }
            else if (optind <= args.Count) sh.SetVar("OPTARG", args[optind++ - 1]);
            else if (silent) { sh.SetVar("OPTARG", c.ToString()); return Done(0, ":"); }
            else
            {
                ctx.Stderr.WriteLine(CommandSpec.Message("getopts", "missingArgument", ("script", sh.GetVariable("0")), ("option", c.ToString())));
                sh.Unset("OPTARG");
                return Done(0, "?");
            }
            return Done(0, c.ToString());
        });

        shell.Register("exec", async ctx =>
        {
            // exec with only redirections is handled by the shell; with a command, the command's status ends the shell
            var args = ctx.Args.SkipWhile(a => a is "-c" or "-l").ToList();
            if (args.Count == 0) return 0;
            throw new ExitSignal(await ctx.Shell.InvokeAsync(args, ctx.Io, ctx.CancellationToken, skipFunctions: true));
        });

        shell.Register("hash", ctx =>
        {
            var sh = ctx.Shell;
            sh.SyncHashed();
            int code = 0, i = 0;
            bool show = false, delete = false, list = false;
            string? path = null;
            for (; i < ctx.Args.Count && ctx.Args[i].Length > 1 && ctx.Args[i][0] == '-'; i++)
            {
                if (ctx.Args[i] == "--") { i++; break; }
                foreach (var ch in ctx.Args[i][1..])
                    if (ch == 'r') sh.Hashed.Clear();
                    else if (ch == 't') show = true;
                    else if (ch == 'd') delete = true;
                    else if (ch == 'p') { path = i + 1 < ctx.Args.Count ? ctx.Args[++i] : throw new UsageException(CommandSpec.Message("hash", "builtinOptionArgument", ("option", "-p"))); break; }
                    else if (ch == 'l') list = true;
                    else throw new UsageException(CommandSpec.Message("hash", "builtinInvalidOption", ("option", $"-{ch}")));
            }
            var names = ctx.Args.Skip(i).ToList();
            if ((show || delete) && names.Count == 0)
            {
                ctx.Stderr.WriteLine($"{sh.ErrPrefix}hash: " + CommandSpec.Message("hash", "builtinOptionArgument", ("option", show ? "-t" : "-d")));
                return 1;
            }
            // -l: each entry as the command that would hash it again
            static string Reusable(string name, string path) => $"builtin hash -p {path} {name}";
            if (list && names.Count == 0) ctx.Stdout.Write(string.Concat(sh.Hashed.Select(h => Reusable(h.Key, h.Value.Path) + "\n")));
            else if (i == 0 && names.Count == 0)
            {
                if (sh.Hashed.Count == 0) ctx.Stdout.WriteLine("hash: hash table empty");
                else ctx.Stdout.Write("hits\tcommand\n" + string.Concat(sh.Hashed.Values.Select(h => $"{h.Hits,4}\t{h.Path}\n")));
            }
            foreach (var n in names)
            {
                if (show && sh.HashLookup(n) is { } hashed) ctx.Stdout.WriteLine(list ? Reusable(n, hashed) : names.Count > 1 ? $"{n}\t{hashed}" : hashed);
                else if (delete && sh.Hashed.Remove(n)) { }
                else if (path != null) sh.Hashed[n] = (path, 0);
                else if (show || delete) { ctx.Stderr.WriteLine($"{sh.ErrPrefix}hash: " + CommandSpec.Message("hash", "notFound", ("name", n))); code = 1; }
                else if (n.Contains('/') || IsShellBuiltin(sh, n) || sh.Functions.ContainsKey(n) || sh.IsUnfiled(n)) { }
                else if (sh.SearchPathAll(n).FirstOrDefault() is { } found) sh.Hashed[n] = (found, 0);
                else { ctx.Stderr.WriteLine($"{sh.ErrPrefix}hash: " + CommandSpec.Message("hash", "notFound", ("name", n))); code = 1; }
            }
            return code;
        });

        shell.Register("builtin", ctx => ctx.Args.Count == 0 ? Task.FromResult(0)
            : IsShellBuiltin(ctx.Shell, ctx.Args[0]) ? ctx.Shell.InvokeAsync(ctx.Args, ctx.Io, ctx.CancellationToken, skipFunctions: true)
            : throw new ShellException(CommandSpec.Message("builtin", "notBuiltin", ("name", ctx.Args[0]))));

        shell.Register("eval", ctx => ctx.Shell.RunScriptTextAsync(string.Join(' ', ctx.Args), ctx.Io, subshell: false, ctx.CancellationToken, eval: true));

        shell.Register("let", ctx =>
        {
            if (ctx.Args.Count == 0) throw new ShellException(CommandSpec.Message("let", "expressionExpected"));
            long v = 0;
            foreach (var expr in ctx.Args) v = Arith.Eval(ctx.Shell, expr);
            return v != 0 ? 0 : 1;
        });

        shell.Register("command", async ctx =>
        {
            var args = ctx.Args.SkipWhile(a => a == "-p").ToList();
            bool describe = args.Count > 0 && args[0] is "-v" or "-V";
            if (!describe && args.Count == 0) return 0;
            if (!describe && args.Count < ctx.Args.Count)
            {
                // -p: search the system's standard PATH instead of the user's
                // shortcut: the hash table is emptied by the PATH switch, as if PATH had been assigned
                var env = ctx.Shell.Environment;
                var saved = env.GetValueOrDefault("PATH");
                env["PATH"] = "/bin:/usr/bin";
                try { return await ctx.Shell.InvokeAsync(args, ctx.Io, ctx.CancellationToken, skipFunctions: true); }
                finally { if (saved == null) env.Remove("PATH"); else env["PATH"] = saved; }
            }
            if (!describe) return await ctx.Shell.InvokeAsync(args, ctx.Io, ctx.CancellationToken, skipFunctions: true);
            int code = 0;
            foreach (var n in args.Skip(1))
            {
                if (args[0] == "-V") { if (!Describe(ctx, n, false)) code = 1; continue; }
                if (ctx.Shell.Aliases.TryGetValue(n, out var al)) ctx.Stdout.WriteLine($"alias {n}='{al}'");
                else if (ShellKeywords.Contains(n) || ctx.Shell.Functions.ContainsKey(n) || IsShellBuiltin(ctx.Shell, n)) ctx.Stdout.WriteLine(n);
                else if (ctx.Shell.Locate(n) is { } l) ctx.Stdout.WriteLine(l.Path);
                else code = 1;
            }
            return code;
        });
    }
}

internal static class Test
{
    public static bool Unary(Shell shell, string op, string arg)
    {
        var path = shell.ResolvePath(arg);
        var fs = shell.Fs;
        return op switch
        {
            "-z" => arg.Length == 0,
            "-n" => arg.Length > 0,
            "-e" => fs.Exists(path),
            "-r" => fs.CanRead(path),
            "-w" => fs.CanWrite(path),
            "-x" => fs.CanExecute(path),
            "-f" => fs.IsFile(path),
            "-d" => fs.IsDirectory(path),
            "-s" => fs.IsFile(path) && fs.Stat(path).Size > 0,
            "-L" or "-h" => fs.IsSymlink(path),
            "-b" or "-c" or "-p" or "-S" or "-t" or "-g" or "-u" or "-k" => false, // no devices, pipes, sockets, terminals or special bits
            "-O" or "-G" or "-N" => fs.Exists(path),
            "-v" => shell.IsVariableSet(arg),
            "-o" => shell.OptionList.Any(o => o.Name == arg && o.On),
            _ => throw new ShellException(CommandSpec.Message("test", "unaryExpected", ("op", op)), 2),
        };
    }

    static readonly HashSet<string> BinaryOps = ["=", "==", "!=", "<", ">", "-eq", "-ne", "-lt", "-le", "-gt", "-ge", "-nt", "-ot", "-ef"];

    /// <summary>-nt / -ot: a missing file is older than any existing one, as in bash.</summary>
    internal static bool Newer(Shell sh, string left, string right)
    {
        DateTime? Time(string p) => sh.Fs.Exists(sh.ResolvePath(p)) ? sh.Fs.Stat(sh.ResolvePath(p)).Modified : null;
        return Time(left) is { } l && (Time(right) is not { } r || l > r);
    }

    static bool Binary(Shell sh, string left, string op, string right)
    {
        switch (op)
        {
            case "=" or "==": return left == right;
            case "!=": return left != right;
            case "<": return string.CompareOrdinal(left, right) < 0;
            case ">": return string.CompareOrdinal(left, right) > 0;
            case "-nt": return Newer(sh, left, right);
            case "-ot": return Newer(sh, right, left);
            case "-ef": return sh.Fs.SameFile(sh.ResolvePath(left), sh.ResolvePath(right));
        }
        bool okL = long.TryParse(left.Trim(), out var l), okR = long.TryParse(right.Trim(), out var r);
        if (!okL || !okR) throw new ShellException(CommandSpec.Message("test", "integerExpected", ("value", okL ? right : left)), 2);
        return op switch { "-eq" => l == r, "-ne" => l != r, "-lt" => l < r, "-le" => l <= r, "-gt" => l > r, _ => l >= r };
    }

    /// <summary>test / [ ]: POSIX rules for up to 3 arguments, then ! ( ) -a -o with the usual precedence.</summary>
    public static bool Evaluate(CommandContext ctx, List<string> a)
    {
        int n = a.Count;
        switch (n)
        {
            case 0: return false;
            case 1: return a[0].Length > 0;
            case 2: return a[0] == "!" ? a[1].Length == 0 : Unary(ctx.Shell, a[0], a[1]);
            case 3:
                if (BinaryOps.Contains(a[1])) return Binary(ctx.Shell, a[0], a[1], a[2]);
                if (a[1] is "-a" or "-o") return a[1] == "-a" ? a[0].Length > 0 && a[2].Length > 0 : a[0].Length > 0 || a[2].Length > 0;
                if (a[0] == "!") return !Evaluate(ctx, a.Skip(1).ToList());
                if (a[0] == "(" && a[2] == ")") return a[1].Length > 0;
                throw new ShellException(CommandSpec.Message("test", "binaryExpected", ("op", a[1])), 2);
            case 4 when a[0] == "!": return !Evaluate(ctx, a.Skip(1).ToList());
        }
        int pos = 0;
        string? Peek() => pos < n ? a[pos] : null;
        bool Or()
        {
            var v = And();
            while (Peek() == "-o") { pos++; var r = And(); v = v || r; }
            return v;
        }
        bool And()
        {
            var v = Not();
            while (Peek() == "-a") { pos++; var r = Not(); v = v && r; }
            return v;
        }
        bool Not()
        {
            Shell.CheckStack();
            if (Peek() == "!") { pos++; return !Not(); }
            return Primary();
        }
        bool Primary()
        {
            var t = Peek() ?? throw new ShellException(CommandSpec.Message("test", "argumentExpected"), 2);
            if (t == "(")
            {
                pos++;
                var v = Or();
                if (Peek() != ")") throw new ShellException(CommandSpec.Message("test", "parenExpected"), 2);
                pos++;
                return v;
            }
            if (pos + 2 < n && BinaryOps.Contains(a[pos + 1])) { pos += 3; return Binary(ctx.Shell, t, a[pos - 2], a[pos - 1]); }
            if (t.Length == 2 && t[0] == '-' && pos + 1 < n) { pos += 2; return Unary(ctx.Shell, t, a[pos - 1]); }
            pos++;
            return t.Length > 0;
        }
        var result = Or();
        if (pos < n) throw new ShellException(CommandSpec.Message("test", "tooManyArguments"), 2);
        return result;
    }
}
