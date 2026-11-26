using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Topwater;

internal static partial class Builtins
{
    static int ParseCount(CommandContext ctx, int fallback) =>
        ctx.Args.Count == 0 ? fallback : int.TryParse(ctx.Args[0], out var n) ? n : throw new ShellException(CommandSpec.Message(ctx.Name, "numericArgument", ("value", ctx.Args[0])), 2);

    static TimeSpan ParseDuration(string s)
    {
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^(\d+(?:\.\d+)?)([smhd]?)$");
        if (!m.Success) throw new ShellException(CommandSpec.Message("sleep", "invalidTimeInterval", ("value", s)));
        var n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        return m.Groups[2].Value switch { "m" => TimeSpan.FromMinutes(n), "h" => TimeSpan.FromHours(n), "d" => TimeSpan.FromDays(n), _ => TimeSpan.FromSeconds(n) };
    }

    static int LoopJump(CommandContext ctx, bool isBreak)
    {
        if (ctx.Shell.LoopDepth > 0) throw new LoopControl(isBreak, Math.Max(1, ParseCount(ctx, 1)));
        ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}{ctx.Name}: " + CommandSpec.Message(ctx.Name, "onlyInLoop"));
        return 0;
    }

    static bool EndsWithEscape(string line) => line.Length - line.TrimEnd('\\').Length is var n && n % 2 == 1;

    /// <summary>
    /// `read a b c`: the fields for each name. The last name gets the rest of the line less trailing IFS whitespace,
    /// and a single trailing IFS delimiter is dropped when the rest is just one field ("x,y," gives b=y, as in bash).
    /// </summary>
    static List<string> ReadFields(string line, string ifs, int count)
    {
        var ws = string.Concat(" \t\n".Where(ifs.Contains)).ToCharArray();
        bool IsWs(char c) => Array.IndexOf(ws, c) >= 0;
        var fields = new List<string>();
        int i = 0;
        while (i < line.Length && IsWs(line[i])) i++;
        for (; fields.Count < count - 1 && i < line.Length;)
        {
            int start = i;
            while (i < line.Length && !ifs.Contains(line[i])) i++;
            fields.Add(line[start..i]);
            while (i < line.Length && IsWs(line[i])) i++;
            if (i < line.Length && ifs.Contains(line[i]) && !IsWs(line[i])) { i++; while (i < line.Length && IsWs(line[i])) i++; }
        }
        string TrimWs(string t) => ws.Length == 0 ? t : t.TrimEnd(ws); // TrimEnd() with no characters would trim all whitespace
        var rest = TrimWs(line[i..]);
        if (rest.Length > 0 && ifs.Contains(rest[^1]) && !IsWs(rest[^1]) && TrimWs(rest[..^1]).IndexOfAny(ifs.ToCharArray()) < 0) rest = TrimWs(rest[..^1]);
        if (i < line.Length || fields.Count < count) fields.Add(rest);
        return fields;
    }

    /// <summary>`umask 027`: octal digits up to 07777, of which the permission bits count; null when out of range.</summary>
    static int? OctalMask(string mode)
    {
        int v = 0;
        foreach (var c in mode)
            if (c is < '0' or > '7' || (v = v * 8 + (c - '0')) > 0xFFF) return null;
        return v & 0x1FF;
    }

    /// <summary>`umask u=rwx,g+w,o-r`: clauses change the permissions the mask leaves; null (and the offending character) when malformed.</summary>
    static int? SymbolicMask(string mode, int mask, out char bad, out bool badOperator)
    {
        int allowed = ~mask & 0x1FF, i = 0;
        bad = ' ';
        badOperator = false;
        while (true)
        {
            int who = 0;
            for (; i < mode.Length && "ugoa".Contains(mode[i]); i++) who |= mode[i] switch { 'u' => 0x1C0, 'g' => 0x38, 'o' => 7, _ => 0x1FF };
            if (who == 0) who = 0x1FF;
            if (i >= mode.Length || !"+-=".Contains(mode[i])) { bad = i < mode.Length ? mode[i] : ' '; badOperator = true; return null; }
            while (i < mode.Length && "+-=".Contains(mode[i]))
            {
                char op = mode[i++];
                int bits = 0;
                for (; i < mode.Length && "rwx".Contains(mode[i]); i++) bits |= (mode[i] switch { 'r' => 4, 'w' => 2, _ => 1 }) * 0x49;
                bits &= who;
                allowed = op == '+' ? allowed | bits : op == '-' ? allowed & ~bits : allowed & ~who | bits;
            }
            if (i >= mode.Length) return ~allowed & 0x1FF;
            if (mode[i] != ',') { bad = mode[i]; return null; }
            i++;
        }
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

    /// <summary>declare -f / -F: function definitions (as written) or names.</summary>
    static int PrintFunctions(CommandContext ctx, List<string> names, bool namesOnly)
    {
        var fns = ctx.Shell.Functions;
        int code = 0;
        foreach (var n in names.Count == 0 ? [.. fns.Keys.Order(StringComparer.Ordinal)] : names)
        {
            if (!fns.TryGetValue(n, out var f)) { code = 1; continue; }
            ctx.Stdout.WriteLine(namesOnly ? (names.Count == 0 ? $"declare -f {n}" : n) : $"{n} () \n{f.Source}");
        }
        return code;
    }

    /// <summary>A variable's attribute letters in bash's order (a A i n r t x l u).</summary>
    internal static string AttributeFlags(Shell sh, string name)
    {
        var attrs = sh.VarAttrs.GetValueOrDefault(name, "");
        sh.Arrays.TryGetValue(name, out var array);
        return string.Concat("aAinrtxlu".Where(c => c == 'a' ? array is { Assoc: false } : c == 'A' ? array is { Assoc: true } : c == 'x' ? sh.IsExported(name) : attrs.Contains(c)));
    }

    /// <summary>`declare -p NAME`: a line that recreates the variable when evaluated; null when there is no such variable.</summary>
    internal static string? DeclareLine(Shell sh, string name)
    {
        var flags = AttributeFlags(sh, name);
        var head = $"declare -{(flags.Length == 0 ? "-" : flags)} {name}";
        static string Quote(string v) => v.Any(c => c < 32 || c == 127) ? Printf.ShellQuote(v)
            : "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("$", "\\$").Replace("`", "\\`") + "\"";
        if (sh.Arrays.TryGetValue(name, out var a))
        {
            if (a.Items.Count == 0) return head + "=()";
            var items = a.Items.Select(kv => $"[{(a.Assoc && !kv.Key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? Quote(kv.Key) : kv.Key)}]={Quote(kv.Value)}");
            return head + "=(" + (a.Assoc ? string.Concat(items.Select(i => i + " ")) : string.Join(' ', items)) + ")";
        }
        return sh.Environment.TryGetValue(name, out var value) ? head + "=" + Quote(value) : flags.Length > 0 ? head : null;
    }

    static void RegisterScriptingCommands(Shell shell)
    {
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

        CommandHandler declare = ctx =>
        {
            var sh = ctx.Shell;
            string on = ctx.Name == "readonly" ? "r" : "", off = "", listOnly = "";
            bool print = false, functions = false, functionNames = false, global = false;
            var names = new List<string>();
            foreach (var a in ctx.Args)
            {
                if (names.Count > 0 || a.Length < 2 || a[0] is not ('-' or '+') || a == "--") { if (a != "--" || names.Count > 0) names.Add(a); continue; }
                foreach (var c in a[1..])
                    switch (c)
                    {
                        case 'p': print = true; break;
                        case 'f': functions = true; break;
                        case 'F': functionNames = true; break;
                        case 'g': global = true; break;
                        case 'a' or 'A': listOnly += c; break; // arrays were declared before the built-in ran
                        case 't': break;
                        case 'i' or 'r' or 'x' or 'l' or 'u' or 'n': if (a[0] == '-') on += c; else off += c; break;
                        default: throw new ShellException(CommandSpec.Message(ctx.Name, "builtinInvalidOption", ("option", $"-{c}")), 2);
                    }
            }
            if (functions || functionNames) return Task.FromResult(PrintFunctions(ctx, names, functionNames));
            if (names.Count == 0)
            {
                // declare / declare -p / readonly / declare -x ...: list what has those attributes
                foreach (var n in sh.Environment.Keys.Concat(sh.Arrays.Keys).Distinct().Order(StringComparer.Ordinal))
                    if ((on + listOnly).All(c => AttributeFlags(sh, n).Contains(c)) && DeclareLine(sh, n) is { } line)
                        ctx.Stdout.WriteLine(print || on.Length + listOnly.Length > 0 ? line : $"{n}={(sh.Arrays.ContainsKey(n) ? line[(line.IndexOf('=') + 1)..] : Printf.ShellQuote(sh.Environment[n]))}");
                return Task.FromResult(0);
            }
            int code = 0;
            if (print)
            {
                foreach (var n in names)
                    if (DeclareLine(sh, n) is { } line) ctx.Stdout.WriteLine(line);
                    else { ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}{ctx.Name}: " + CommandSpec.Message(ctx.Name, "notFound", ("name", n))); code = 1; }
                return Task.FromResult(code);
            }
            bool local = ctx.Name == "local" || ctx.Name is "declare" or "typeset" && sh.InFunction && !global;
            foreach (var arg in names)
            {
                int eq = arg.IndexOf('=');
                bool append = eq > 0 && arg[eq - 1] == '+';
                var name = eq > 0 ? arg[..(append ? eq - 1 : eq)] : arg;
                var value = eq > 0 ? arg[(eq + 1)..] : null;
                try
                {
                    if (!Topwater.Parsing.Parser.IsName(name)) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.NotValidIdentifier, ("name", arg)));
                    if (local) sh.DeclareLocal(name, null); // shortcut: `local x` is empty here, unset in bash
                    if (on.Contains('n'))
                    {
                        // a nameref holds the name of the variable it stands for
                        sh.SetAttrs(name, "n");
                        sh.MarkShellVariable(name);
                        if (value != null) sh.Environment[name] = value;
                        continue;
                    }
                    var target = sh.Resolve(name);
                    sh.SetAttrs(target, on.Replace("r", ""), off);
                    if (value != null) sh.SetVar(name, value, append);
                    else if (!sh.Environment.ContainsKey(target) && !sh.Arrays.ContainsKey(target)) { sh.MarkShellVariable(target); sh.Environment[target] = ""; }
                    if (on.Contains('r')) sh.SetAttrs(target, "r");
                }
                catch (ShellException e) { ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}{ctx.Name}: {e.Message}"); code = 1; }
            }
            return Task.FromResult(code);
        };
        shell.Register("local", declare);
        shell.Register("declare", declare);
        shell.Register("typeset", declare);
        shell.Register("readonly", declare);

        shell.Register("read", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("read"));
            var sh = ctx.Shell;
            var names = a.Positional.Count > 0 ? a.Positional : ["REPLY"];
            var input = ctx.StdinBuffer;
            if (a.Value('u') is { } u && u != "0")
                input = int.TryParse(u, out var fd) && (ctx.Fds?.GetValueOrDefault(fd) as StdinBuffer ?? sh.InputFd(fd)) is { } fdInput ? fdInput : throw new ShellException(CommandSpec.Message(ctx.Name, "badFileDescriptor", ("fd", u)));
            var delim = a.Value('d') is { } dv ? (dv.Length == 0 ? '\0' : dv[0]) : '\n';
            string? line;
            if (a.Value('N') is { } exact) line = input.ReadChars(int.Parse(exact), null);
            else if (a.Value('n') is { } upTo) line = input.ReadChars(int.Parse(upTo), delim);
            else
            {
                line = input.ReadLine(delim);
                // without -r, a backslash at the end of a line joins it to the next one
                while (!a.Has('r') && line != null && input.LastTerminated && delim == '\n' && EndsWithEscape(line) && input.ReadLine() is { } more)
                    line = line[..^1] + more;
            }
            if (line == null) { foreach (var n in names) sh.SetVar(n, ""); return 1; }
            if (!a.Has('r')) line = System.Text.RegularExpressions.Regex.Replace(line, @"\\(.)", "$1");
            var ifs = sh.Ifs;
            if (a.Has('a'))
            {
                // read -a NAME: every field becomes an element
                sh.Unset(names[0]);
                var array = sh.Writable(names[0]);
                foreach (var field in Shell.SplitFields(line, ifs)) { sh.ChargeVariable(names[0], field.Length, 0); array.Set(array.Next.ToString(), field); }
            }
            else if (a.Positional.Count == 0) sh.SetVar("REPLY", line);
            else
            {
                var fields = ReadFields(line, ifs, names.Count);
                for (int i = 0; i < names.Count; i++) sh.SetVar(names[i], i < fields.Count ? fields[i] : "");
            }
            // like bash, a final line with no trailing newline is still assigned, but read reports end of input
            return input.LastTerminated ? 0 : 1;
        });

        CommandHandler mapfile = ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of(ctx.Name));
            var sh = ctx.Shell;
            var name = a.Positional.Count > 0 ? a.Positional[0] : "MAPFILE";
            var input = ctx.StdinBuffer;
            if (a.Value('u') is { } u && u != "0")
                input = int.TryParse(u, out var fd) && (ctx.Fds?.GetValueOrDefault(fd) as StdinBuffer ?? sh.InputFd(fd)) is { } fdInput ? fdInput : throw new ShellException(CommandSpec.Message(ctx.Name, "badFileDescriptor", ("fd", u)));
            var delim = a.Value('d') is { } dv ? (dv.Length == 0 ? '\0' : dv[0]) : '\n';
            long max = a.Value('n') is { } n ? long.Parse(n) : 0, skip = a.Value('s') is { } sk ? long.Parse(sk) : 0;
            long index = a.Value('O') is { } origin ? long.Parse(origin) : 0;
            if (a.Value('O') == null) sh.Unset(name); // -O keeps what the array already holds
            var array = sh.Writable(name);
            for (long count = 0; (max == 0 || count < max) && input.ReadLine(delim) is { } line;)
            {
                if (skip > 0) { skip--; continue; }
                var item = a.Has('t') || !input.LastTerminated ? line : line + delim;
                sh.ChargeVariable(name, item.Length, 0);
                array.Set((index++).ToString(), item);
                count++;
            }
            return Task.FromResult(0);
        };
        shell.Register("mapfile", mapfile);
        shell.Register("readarray", mapfile);

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

        shell.Register("wait", ctx =>
        {
            // background jobs ran to completion when they were started: wait just reports their status
            int code = 0;
            var ids = ctx.Args.Where(a => !a.StartsWith('-')).ToList();
            if (ctx.Args.Contains("-n") && ids.Count == 0) return ctx.Shell.WaitNext();
            if (ids.Count == 0) ctx.Shell.ForgetJobs();
            foreach (var id in ids)
            {
                if (id.StartsWith('%')) { code = 0; continue; }
                if (!int.TryParse(id, out var pid)) { ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}wait: " + CommandSpec.Message("wait", "notPid", ("id", id))); code = 2; continue; }
                code = ctx.Shell.WaitFor(pid);
                if (code == 127) ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}wait: " + CommandSpec.Message("wait", "notChild", ("pid", pid.ToString())));
            }
            return code;
        });
        shell.Register("jobs", _ => 0);
        shell.Register("disown", _ => 0);

        shell.Register("kill", ctx =>
        {
            if (ctx.Args is ["-l", ..]) { ctx.Stdout.WriteLine(" 1) SIGHUP\t 2) SIGINT\t 3) SIGQUIT\t 9) SIGKILL\t15) SIGTERM"); return 0; }
            int signal = 15, code = 0;
            var targets = new List<string>();
            for (int i = 0; i < ctx.Args.Count; i++)
            {
                var a = ctx.Args[i];
                if (a is "-s" or "-n" && i + 1 < ctx.Args.Count) { a = "-" + ctx.Args[++i]; }
                if (a.StartsWith('-') && targets.Count == 0)
                {
                    var sig = a[1..].ToUpperInvariant().Replace("SIG", "");
                    signal = int.TryParse(sig, out var num) ? num : sig switch { "HUP" => 1, "INT" => 2, "QUIT" => 3, "KILL" => 9, "TERM" => 15, _ => 15 };
                    continue;
                }
                targets.Add(a);
            }
            if (targets.Count == 0) throw new ShellException(CommandSpec.Message("kill", "usage"), 2);
            foreach (var t in targets)
            {
                if (t == "4242") { if (signal != 0) throw new ExitSignal(128 + signal); continue; } // the shell itself
                ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}kill: " + (t.StartsWith('%') ? CommandSpec.Message("kill", "noSuchJob", ("job", t)) : CommandSpec.Message("kill", "noSuchProcess", ("pid", t))));
                code = 1;
            }
            return code;
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

        shell.Register("ulimit", ctx =>
        {
            if (ctx.Args.Any(a => !a.StartsWith('-'))) return 0; // setting a limit: accepted
            ctx.Stdout.WriteLine(ctx.Args.Contains("-n") ? "1024" : ctx.Args.Contains("-s") ? "8192" : ctx.Args.Contains("-c") ? "0" : "unlimited");
            return 0;
        });
        shell.Register("times", ctx => { ctx.Stdout.Write("0m0.010s 0m0.005s\n0m0.000s 0m0.000s\n"); return 0; });
        shell.Register("umask", ctx =>
        {
            bool symbolic = false, reusable = false;
            int i = 0;
            for (; i < ctx.Args.Count && ctx.Args[i].Length > 1 && ctx.Args[i][0] == '-'; i++)
            {
                if (ctx.Args[i] == "--") { i++; break; }
                foreach (var c in ctx.Args[i][1..])
                    if (c == 'S') symbolic = true;
                    else if (c == 'p') reusable = true;
                    else
                    {
                        ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}umask: " + CommandSpec.Message("umask", "builtinInvalidOption", ("option", "-" + c)));
                        ctx.Stderr.WriteLine("umask: " + CommandSpec.Message("umask", "usage"));
                        return 2;
                    }
            }
            if (i < ctx.Args.Count)
            {
                var mode = ctx.Args[i];
                char bad = ' ';
                bool badOperator = false, octal = mode.Length > 0 && char.IsAsciiDigit(mode[0]);
                int? mask = octal ? OctalMask(mode) : SymbolicMask(mode, ctx.Fs.Umask, out bad, out badOperator);
                if (mask == null)
                {
                    ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}umask: " + (octal
                        ? CommandSpec.Message("umask", "octalOutOfRange", ("value", mode))
                        : CommandSpec.Message("umask", badOperator ? "invalidOperator" : "invalidSymbolic", ("char", bad.ToString()))));
                    return 1;
                }
                ctx.Fs.Umask = mask.Value;
                if (!symbolic) return 0;
            }
            int allowed = ~ctx.Fs.Umask & 0x1FF;
            string Perms(int shift) => string.Concat("rwx".Where((_, k) => (allowed >> (shift + 2 - k) & 1) != 0));
            var shown = symbolic ? $"u={Perms(6)},g={Perms(3)},o={Perms(0)}" : Convert.ToString(ctx.Fs.Umask, 8).PadLeft(4, '0');
            ctx.Stdout.WriteLine(reusable ? $"umask {(symbolic ? "-S " : "")}{shown}" : shown);
            return 0;
        });

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

        shell.Register("printenv", ctx =>
        {
            if (ctx.Args.Count == 0)
            {
                foreach (var (k, v) in ctx.Shell.Environment.Where(e => ctx.Shell.IsExported(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal)) ctx.Stdout.WriteLine($"{k}={v}");
                return 0;
            }
            int code = 0;
            foreach (var n in ctx.Args)
                if (ctx.Shell.IsExported(n) && ctx.Shell.Environment.TryGetValue(n, out var v)) ctx.Stdout.WriteLine(v); else code = 1;
            return code;
        });

        shell.Register("sleep", async ctx =>
        {
            if (ctx.Args.Count == 0) throw new UsageException(CommandSpec.Message("sleep", "missingOperand"));
            var total = ctx.Args.Aggregate(TimeSpan.Zero, (t, a) => t + ParseDuration(a));
            await ctx.Shell.SleepAsync(total, ctx.CancellationToken);
            return 0;
        });

        shell.Register("timeout", async ctx =>
        {
            var args = ctx.Args.SkipWhile(a => a.StartsWith('-')).ToList();
            if (args.Count < 2) throw new ShellException(CommandSpec.Message("timeout", "missingOperand"), 125);
            if (ExecFailure(ctx, args[1], out _) is { } error)
            {
                ctx.Stderr.WriteLine("timeout: " + CommandSpec.Message("timeout", "failedToRun", ("command", args[1]), ("errno", error)));
                return error == CommandSpec.Errno.EACCES ? 126 : 127;
            }
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ctx.CancellationToken);
            cts.CancelAfter(ParseDuration(args[0]));
            try { return await ctx.Shell.WithVirtualTimeoutAsync(ParseDuration(args[0]), () => ctx.Shell.InvokeAsync(args.Skip(1).ToList(), ctx.Io, cts.Token)) ?? 124; }
            catch (OperationCanceledException) when (!ctx.CancellationToken.IsCancellationRequested) { return 124; }
        });

        shell.Register("time", async ctx =>
        {
            var elapsed = ctx.Shell.StartTimer();
            int code = ctx.Args.Count == 0 ? 0 : await ctx.Shell.InvokeAsync(ctx.Args.ToList(), ctx.Io, ctx.CancellationToken);
            var t = elapsed();
            ctx.Stderr.Write($"\nreal\t{(int)t.TotalMinutes}m{t.TotalSeconds % 60:0.000}s\nuser\t0m0.000s\nsys\t0m0.000s\n");
            return code;
        });

        shell.Register("clear", _ => 0);
    }

    static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    static void RegisterUtilityCommands(Shell shell)
    {
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

        shell.Register("stat", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("stat"));
            int code = 0;
            foreach (var p in a.Positional)
            {
                var abs = ctx.ResolvePath(p);
                bool follow = a.Has('L');
                if (!ctx.Fs.Exists(abs, follow)) { ctx.Stderr.WriteLine("stat: " + CommandSpec.Message("stat", "cannotStat", ("file", p), ("errno", ctx.Fs.WhyMissing(abs, follow)))); code = 1; continue; }
                var st = ctx.Fs.Stat(abs, follow);
                var type = st.IsSymlink ? "symbolic link" : st.IsDirectory ? "directory" : st.Size == 0 ? "regular empty file" : "regular file";
                var named = st.LinkTarget is { } target ? $"{QuoteAlways(p)} -> {QuoteAlways(target)}" : QuoteAlways(p);
                var mode = Convert.ToString(st.Mode, 8).PadLeft(3, '0');
                var rwx = VirtualFileSystem.FormatMode(st);
                var owner = st.Owner;
                var uid = owner == "root" ? "0" : "1000";
                var mtime = st.Modified.ToString("yyyy-MM-dd HH:mm:ss.fffffff00 +0000", CultureInfo.InvariantCulture);
                if (a.Value('c') is { } fmt)
                {
                    var sb = new StringBuilder();
                    for (int i = 0; i < fmt.Length; i++)
                    {
                        if (fmt[i] != '%' || i + 1 >= fmt.Length) { sb.Append(fmt[i]); continue; }
                        sb.Append(fmt[++i] switch
                        {
                            'n' => p, 'N' => named, 's' => st.Size.ToString(), 'F' => type, 'a' => mode, 'A' => rwx, 'U' => owner, 'G' => owner,
                            'u' => uid, 'g' => uid, 'y' => mtime, 'Y' => new DateTimeOffset(st.Modified).ToUnixTimeSeconds().ToString(),
                            'b' => st.Blocks.ToString(), 'B' => "512", 'h' => st.Links.ToString(), 'i' => (Math.Abs(abs.GetHashCode()) % 1000000).ToString(), '%' => "%",
                            var c => "%" + c,
                        });
                    }
                    ctx.Stdout.WriteLine(sb.ToString());
                    continue;
                }
                ctx.Stdout.Write(
                    $"  File: {p}{(st.IsSymlink ? " -> " + st.LinkTarget : "")}\n  Size: {st.Size,-10}\tBlocks: {st.Blocks,-10} IO Block: 4096   {type}\n" +
                    $"Access: ({mode.PadLeft(4, '0')}/{rwx})  Uid: ({uid,5}/{owner,8})   Gid: ({uid,5}/{owner,8})\n" +
                    $"Modify: {mtime}\nChange: {mtime}\n");
            }
            return code;
        });

        shell.Register("du", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("du"));
            int maxDepth = a.Has('s') ? 0 : a.Value('d') is { } d ? int.Parse(d) : int.MaxValue;
            string Fmt(long kb) => !a.Has('h') ? kb.ToString()
                : kb < 10 ? $"{kb:0.0}K" : kb < 1024 ? $"{kb}K" : kb < 1024 * 1024 ? $"{kb / 1024.0:0.#}M" : $"{kb / 1048576.0:0.#}G";
            long total = 0;
            int code = 0;
            long Walk(string abs, string display, int depth)
            {
                if (!ctx.Fs.IsDirectory(abs, follow: false))
                {
                    long kb = ctx.Fs.Stat(abs, follow: false).Blocks / 2;
                    if (a.Has('a') && depth <= maxDepth || depth == 0) ctx.Stdout.WriteLine($"{Fmt(kb)}\t{display}");
                    return kb;
                }
                long sum = 4;
                foreach (var n in ctx.Fs.List(abs)) sum += Walk(VirtualFileSystem.Normalize(abs, n), display.TrimEnd('/') + "/" + n, depth + 1);
                if (depth <= maxDepth) ctx.Stdout.WriteLine($"{Fmt(sum)}\t{display}");
                return sum;
            }
            foreach (var p in a.Positional.DefaultIfEmpty("."))
            {
                var abs = ctx.ResolvePath(p);
                if (!ctx.Fs.Exists(abs, follow: false)) { ctx.Stderr.WriteLine("du: " + CommandSpec.Message("du", "cannotAccess", ("file", p), ("errno", "No such file or directory"))); code = 1; continue; }
                total += Walk(abs, p, 0);
            }
            if (a.Has('c')) ctx.Stdout.WriteLine($"{Fmt(total)}\ttotal");
            return code;
        });

        shell.Register("df", ctx =>
        {
            bool h = ctx.Args.Any(x => x.StartsWith('-') && x.Contains('h'));
            // the disk is the filesystem's capacity and what its files really hold
            long size = ctx.Fs.Capacity, used = ctx.Fs.UsedBytes, avail = Math.Max(0, size - used);
            int pct = size == 0 ? 0 : (int)Math.Ceiling(used * 100.0 / size);
            static string H(long b)
            {
                if (b < 1024) return b.ToString();
                double v = b; int u = 0;
                do { v /= 1024; u++; } while (Math.Ceiling(v) >= 1024 && u < 4);
                return (v < 10 ? (Math.Ceiling(v * 10) / 10).ToString("0.0") : Math.Ceiling(v).ToString("0")) + "KMGT"[u - 1]; // df -h rounds up
            }
            ctx.Stdout.Write(h
                ? $"Filesystem      Size  Used Avail Use% Mounted on\n/dev/sda1      {H(size),5} {H(used),5} {H(avail),5} {pct,3}% /\ntmpfs           3.9G     0  3.9G   0% /dev/shm\n"
                : $"Filesystem     1K-blocks    Used Available Use% Mounted on\n/dev/sda1      {size / 1024,10} {(used + 1023) / 1024,7} {avail / 1024,9} {pct,3}% /\ntmpfs            4007440       0   4007440   0% /dev/shm\n");
            return 0;
        });

        shell.Register("chmod", Chmod.Run);

        CommandHandler chown = ctx =>
        {
            var files = ctx.Args.Where(x => !x.StartsWith('-')).Skip(1).ToList(); // first operand is the owner
            if (files.Count == 0) throw new UsageException(CommandSpec.Message(ctx.Name, "missingOperand"));
            var owner = ctx.Args.First(x => !x.StartsWith('-')).Split(':')[0];
            int code = 0;
            foreach (var f in files)
            {
                var path = ctx.ResolvePath(f);
                if (!ctx.Fs.Exists(path)) { ctx.Stderr.WriteLine($"{ctx.Name}: " + CommandSpec.Message(ctx.Name, "cannotAccess", ("file", f), ("reason", ctx.Fs.WhyMissing(path)))); code = 1; continue; }
                try { ctx.Fs.SetOwner(path, owner.Length > 0 ? owner : ctx.Fs.Stat(path).Owner, ctx.Args.Contains("-R")); }
                catch (ShellException) { ctx.Stderr.WriteLine($"{ctx.Name}: changing ownership of '{f}': Operation not permitted"); code = 1; }
            }
            return Task.FromResult(code);
        };
        shell.Register("chown", chown);
        shell.Register("chgrp", chown);

        // the agent is a regular user: privilege escalation fails the way it does on a real server
        shell.Register("sudo", ctx =>
        {
            var user = ctx.Shell.Options.User;
            if (ctx.Args is ["-V"] or ["--version"]) { ctx.Stdout.WriteLine("Sudo version 1.9.15p5"); return 0; }
            if (ctx.Args.Count == 0) { ctx.Stderr.WriteLine(CommandSpec.Message("sudo", "usage")); return 1; }
            if (ctx.Args[0] is "-l" or "--list") { ctx.Stderr.WriteLine(CommandSpec.Message("sudo", "listDenied", ("user", user), ("host", ctx.Shell.Options.Hostname))); return 1; }
            ctx.Stderr.WriteLine(CommandSpec.Message("sudo", "notSudoer", ("user", user)));
            return 1;
        });
        shell.Register("su", ctx =>
        {
            ctx.Stderr.WriteLine(CommandSpec.Message("su", "authenticationFailure"));
            return 1;
        });

        shell.Register("nl", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("nl"));
            bool all = a.Value('b') == "a";
            int width = int.Parse(a.Value('w') ?? "6"), n = 0, code = 0;
            var sep = a.Value('s') ?? "\t";
            foreach (var (_, text) in Inputs(ctx, a.Positional, () => code = 1))
                foreach (var line in SplitLines(text))
                    ctx.Stdout.WriteLine(line.Length == 0 && !all ? "" : $"{(++n).ToString().PadLeft(width)}{sep}{line}");
            return code;
        });

        CommandHandler pager = ctx =>
        {
            int code = 0;
            foreach (var (_, text) in Inputs(ctx, ctx.Args.Where(x => !x.StartsWith('-') && !x.StartsWith('+')).ToList(), () => code = 1)) ctx.Stdout.Write(text);
            return Task.FromResult(code);
        };
        shell.Register("less", pager);
        shell.Register("more", pager);

        shell.Register("file", ctx =>
        {
            int code = 0;
            foreach (var p in ctx.Args.Where(x => !x.StartsWith('-')))
            {
                var abs = ctx.ResolvePath(p);
                string desc;
                if (!ctx.Fs.Exists(abs)) { desc = CommandSpec.Message("file", "cannotOpen", ("file", p)); code = 1; }
                else if (ctx.Fs.IsDirectory(abs)) desc = "directory";
                else
                {
                    var data = ctx.Fs.ReadAllBytes(abs);
                    var t = Encoding.UTF8.GetString(data);
                    desc = t.Length == 0 ? "empty"
                        : MarkdownConverter.FileType(data) is { } magic ? magic
                        : t.StartsWith("\u007fELF") ? "ELF 64-bit LSB pie executable, x86-64, version 1 (SYSV), dynamically linked, stripped"
                        : t.StartsWith("#!") ? (t.Contains("python") ? "Python script" : "Bourne-Again shell script") + ", ASCII text executable"
                        : LooksJson(t) ? "JSON text data"
                        : t.Contains('\0') || !Encoding.UTF8.GetBytes(t).AsSpan().SequenceEqual(data) ? "data" // NUL bytes or invalid UTF-8
                        : t.Any(c => c > 127) ? "Unicode text, UTF-8 text" : "ASCII text";
                }
                ctx.Stdout.WriteLine($"{p}: {desc}");
            }
            return code;
        });

        shell.Register("rev", ctx =>
        {
            int code = 0;
            foreach (var (_, text) in Inputs(ctx, ctx.Args, () => code = 1))
                foreach (var line in SplitLines(text)) ctx.Stdout.WriteLine(new string(line.Reverse().ToArray()));
            return code;
        });

        shell.Register("tac", ctx =>
        {
            int code = 0;
            foreach (var (_, text) in Inputs(ctx, ctx.Args, () => code = 1)) WriteLines(ctx.Stdout, Enumerable.Reverse(SplitLines(text)));
            return code;
        });

        shell.Register("paste", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("paste"));
            var delims = Unescape(a.Value('d') ?? "\t");
            if (delims.Length == 0) delims = "\0";
            char D(int i) => delims[i % delims.Length];
            string Join(IList<string> cells) { var sb = new StringBuilder(); for (int i = 0; i < cells.Count; i++) { if (i > 0 && D(i - 1) != '\0') sb.Append(D(i - 1)); sb.Append(cells[i]); } return sb.ToString(); }
            int code = 0;
            List<List<string>> columns;
            int dashes = a.Positional.Count(f => f == "-");
            if (dashes > 1 && !a.Has('s'))
            {
                // several "-" operands take turns reading lines from stdin (seq 6 | paste - - -)
                var stdinLines = SplitLines(ctx.StdinText);
                columns = [];
                int k = 0;
                foreach (var f in a.Positional)
                {
                    if (f == "-") { int start = k++; columns.Add(stdinLines.Where((_, idx) => idx % dashes == start).ToList()); }
                    else columns.AddRange(Inputs(ctx, [f], () => code = 1).Select(i => SplitLines(i.Text)));
                }
            }
            else columns = Inputs(ctx, a.Positional, () => code = 1).Select(i => SplitLines(i.Text)).ToList();
            if (a.Has('s')) foreach (var col in columns) ctx.Stdout.WriteLine(Join(col));
            else
            {
                int rows = columns.Count == 0 ? 0 : columns.Max(c => c.Count);
                for (int r = 0; r < rows; r++) ctx.Stdout.WriteLine(Join(columns.Select(c => r < c.Count ? c[r] : "").ToList()));
            }
            return code;
        });

        shell.Register("column", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("column"));
            int code = 0;
            var lines = Inputs(ctx, a.Positional, () => code = 1).SelectMany(i => SplitLines(i.Text)).ToList();
            if (!a.Has('t')) { WriteLines(ctx.Stdout, lines); return code; }
            var sep = a.Value('s');
            var rows = lines.Where(l => l.Trim().Length > 0)
                .Select(l => sep != null ? l.Split(Unescape(sep).ToCharArray(), StringSplitOptions.RemoveEmptyEntries) : l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToList();
            var outSep = a.Value('o') ?? "  ";
            int cols = rows.Count == 0 ? 0 : rows.Max(r => r.Length);
            var widths = Enumerable.Range(0, cols).Select(c => rows.Max(r => c < r.Length ? r[c].Length : 0)).ToArray();
            foreach (var r in rows)
                ctx.Stdout.WriteLine(string.Join(outSep, r.Select((cell, i) => i == r.Length - 1 ? cell : cell.PadRight(widths[i]))));
            return code;
        });

        shell.Register("base64", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("base64"));
            int code = 0;
            var data = InputBytes(ctx, a.Positional, () => code = 1).SelectMany(i => i.Text).ToArray();
            if (a.Has('d'))
            {
                try { ctx.Stdout.Write(ByteText.Decode(Convert.FromBase64String(string.Concat(Encoding.UTF8.GetString(data).Where(c => !char.IsWhiteSpace(c)))))); }
                catch (FormatException) { throw new ShellException(CommandSpec.Message("base64", "invalidInput")); }
                return code;
            }
            var enc = Convert.ToBase64String(data);
            int wrap = int.Parse(a.Value('w') ?? "76");
            if (wrap > 0) enc = string.Join('\n', enc.Chunk(wrap).Select(c => new string(c)));
            ctx.Stdout.WriteLine(enc);
            return code;
        });

        foreach (var (name, hash) in new (string, Func<byte[], byte[]>)[] { ("md5sum", MD5.HashData), ("sha1sum", SHA1.HashData), ("sha256sum", SHA256.HashData), ("sha512sum", SHA512.HashData) })
        {
            shell.Register(name, ctx =>
            {
                int code = 0;
                var files = ctx.Args.Where(x => !x.StartsWith('-') || x == "-").ToList();
                foreach (var (n, data) in InputBytes(ctx, files, () => code = 1))
                    ctx.Stdout.WriteLine($"{Hex(hash(data))}  {(files.Count == 0 ? "-" : n)}");
                return code;
            });
        }

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

        shell.Register("seq", ctx =>
        {
            var args = ctx.Args;
            var nums = new List<string>();
            string sep = "\n";
            bool equalWidth = false;
            string? fmt = null;
            for (int i = 0; i < args.Count; i++)
            {
                var x = args[i];
                if (double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) nums.Add(x);
                else if (x is "-w" or "--equal-width") equalWidth = true;
                else if (x == "-s") sep = i + 1 < args.Count ? args[++i] : throw new ShellException(CommandSpec.Message("seq", "optionRequiresArgument", ("option", "s")));
                else if (x.StartsWith("-s")) sep = x[2..];
                else if (x.StartsWith("--separator=")) sep = x[12..];
                else if (x is "-f" or "--format") fmt = i + 1 < args.Count ? args[++i] : throw new ShellException(CommandSpec.Message("seq", "optionRequiresArgument", ("option", "f")));
                else if (x.StartsWith("-f")) fmt = x[2..];
                else if (x.StartsWith("--format=")) fmt = x[9..];
                else throw new ShellException(CommandSpec.Message("seq", "invalidOption", ("option", x.TrimStart('-'))));
            }
            if (nums.Count is 0 or > 3) throw new UsageException(nums.Count == 0 ? CommandSpec.Message("seq", "missingOperand") : CommandSpec.Message("seq", "extraOperand", ("operand", nums[3])));
            var v = nums.Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToList();
            double first = v.Count > 1 ? v[0] : 1, step = v.Count == 3 ? v[1] : 1, last = v[^1];
            if (step == 0) throw new ShellException(CommandSpec.Message("seq", "zeroIncrement"));
            int decimals = nums.Max(x => x.Contains('.') ? x.Length - x.IndexOf('.') - 1 : 0);
            var items = new List<string>();
            for (long k = 0; ; k++)
            {
                double x = first + k * step; // not a running sum, so 0.1 steps don't drift
                if (!(step > 0 ? x <= last + 1e-9 : x >= last - 1e-9)) break;
                items.Add(fmt != null ? Printf.Format(fmt, [x.ToString("R", CultureInfo.InvariantCulture)]) : x.ToString("F" + decimals, CultureInfo.InvariantCulture));
                if (items.Count > 1_000_000) throw new ShellException(CommandSpec.Message("seq", "tooManyNumbers"));
            }
            if (equalWidth && items.Count > 0) { int w = items.Max(s => s.Length); items = items.Select(s => s.PadLeft(w, '0')).ToList(); }
            if (items.Count > 0) ctx.Stdout.Write(string.Join(Unescape(sep), items) + "\n");
            return 0;
        });

        shell.Register("diff", ctx => Diff.Run(ctx));

        // ---------- fake system info ----------
        shell.Register("id", ctx =>
        {
            var u = ctx.Shell.Options.User;
            var a = Args.Parse(ctx.Args, CommandSpec.Of("id"));
            ctx.Stdout.WriteLine(a.Has('u') || a.Has('g') ? (a.Has('n') ? u : "1000") : $"uid=1000({u}) gid=1000({u}) groups=1000({u})");
            return 0;
        });
        shell.Register("uptime", ctx =>
        {
            ctx.Stdout.WriteLine(ctx.Args.Contains("-p") ? "up 3 days, 4 hours, 12 minutes" : $" {ctx.Shell.Now:HH:mm:ss} up 3 days,  4:12,  1 user,  load average: 0.08, 0.03, 0.01");
            return 0;
        });
        shell.Register("free", ctx =>
        {
            bool h = ctx.Args.Any(x => x.Contains('h'));
            ctx.Stdout.Write(h
                ? "               total        used        free      shared  buff/cache   available\nMem:           7.6Gi       1.9Gi       3.8Gi        12Mi       2.1Gi       5.5Gi\nSwap:          2.0Gi          0B       2.0Gi\n"
                : "               total        used        free      shared  buff/cache   available\nMem:         8014880     1992344     3984512       12288     2201024     5748660\nSwap:        2097148           0     2097148\n");
            return 0;
        });
        shell.Register("nproc", ctx => { ctx.Stdout.WriteLine("4"); return 0; });
    }

    static bool LooksJson(string t)
    {
        var s = t.TrimStart();
        if (s.Length == 0 || s[0] is not ('{' or '[')) return false;
        try { JsonDocument.Parse(t); return true; } catch (JsonException) { return false; }
    }
}

internal static class Diff
{
    enum Op { Equal, Delete, Insert }

    public static int Run(CommandContext ctx)
    {
        var a = Args.Parse(ctx.Args, CommandSpec.Of("diff"));
        if (a.Positional.Count != 2) throw new UsageException(a.Positional.Count < 2 ? CommandSpec.Message("diff", "missingOperandAfter", ("operand", a.Positional.LastOrDefault() ?? "diff")) : CommandSpec.Message("diff", "extraOperand", ("operand", a.Positional[2])));
        string Read(string p)
        {
            if (p == "-") return ctx.StdinText;
            var abs = ctx.ResolvePath(p);
            if (!ctx.Fs.Exists(abs)) throw new ShellException(CommandSpec.Message("diff", "fileNotFound", ("file", p)), 2);
            if (ctx.Fs.IsDirectory(abs)) throw new ShellException($"{p}: Is a directory (directory diff is not supported)", 2);
            return ctx.Fs.ReadAllText(abs);
        }
        var (an, bn) = (a.Positional[0], a.Positional[1]);
        var x = Builtins.SplitLines(Read(an));
        var y = Builtins.SplitLines(Read(bn));
        string Key(string l)
        {
            if (a.Has('w')) l = string.Concat(l.Where(c => !char.IsWhiteSpace(c)));
            else if (a.Has('b')) l = System.Text.RegularExpressions.Regex.Replace(l.TrimEnd(), @"\s+", " ");
            return a.Has('i') ? l.ToLowerInvariant() : l;
        }
        var ops = Compute(x.Select(Key).ToList(), y.Select(Key).ToList());
        if (ops.All(o => o.Op == Op.Equal))
        {
            if (a.Has('s')) ctx.Stdout.WriteLine($"Files {an} and {bn} are identical");
            return 0;
        }
        if (a.Has('q')) { ctx.Stdout.WriteLine($"Files {an} and {bn} differ"); return 1; }
        if (a.Has('u') || a.Value('U') != null) Unified(ctx.Stdout, ops, x, y, an, bn, int.Parse(a.Value('U') ?? "3"));
        else Normal(ctx.Stdout, ops, x, y);
        return 1;
    }

    /// <summary>LCS diff. shortcut: O(n*m) memory; switch to Myers if agents diff large files.</summary>
    static List<(Op Op, int A, int B)> Compute(List<string> x, List<string> y)
    {
        if ((long)x.Count * y.Count > 25_000_000) throw new ShellException(CommandSpec.Message("diff", "tooLarge"), 2);
        var lcs = new int[x.Count + 1, y.Count + 1];
        for (int i = x.Count - 1; i >= 0; i--)
            for (int j = y.Count - 1; j >= 0; j--)
                lcs[i, j] = x[i] == y[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var ops = new List<(Op, int, int)>();
        int a = 0, b = 0;
        while (a < x.Count || b < y.Count)
        {
            if (a < x.Count && b < y.Count && x[a] == y[b]) ops.Add((Op.Equal, a++, b++));
            else if (b < y.Count && (a == x.Count || lcs[a, b + 1] >= lcs[a + 1, b])) ops.Add((Op.Insert, a, b++));
            else ops.Add((Op.Delete, a++, b));
        }
        // emit deletions before insertions inside each change block, like GNU diff
        for (int i = 0; i < ops.Count;)
        {
            if (ops[i].Item1 == Op.Equal) { i++; continue; }
            int j = i;
            while (j < ops.Count && ops[j].Item1 != Op.Equal) j++;
            var block = ops.GetRange(i, j - i).OrderBy(o => o.Item1 == Op.Delete ? 0 : 1).ToList();
            for (int k = 0; k < block.Count; k++) ops[i + k] = block[k];
            i = j;
        }
        return ops;
    }

    static string Range(int start, int count) => count == 1 ? $"{start}" : $"{start},{start + count - 1}";

    static void Normal(TextWriter w, List<(Op Op, int A, int B)> ops, List<string> x, List<string> y)
    {
        for (int i = 0; i < ops.Count;)
        {
            if (ops[i].Op == Op.Equal) { i++; continue; }
            int j = i;
            while (j < ops.Count && ops[j].Op != Op.Equal) j++;
            var dels = ops.GetRange(i, j - i).Where(o => o.Op == Op.Delete).ToList();
            var ins = ops.GetRange(i, j - i).Where(o => o.Op == Op.Insert).ToList();
            int aStart = ops.GetRange(i, j - i).Min(o => o.A), bStart = ops.GetRange(i, j - i).Min(o => o.B);
            if (dels.Count > 0 && ins.Count > 0) w.WriteLine($"{Range(aStart + 1, dels.Count)}c{Range(bStart + 1, ins.Count)}");
            else if (dels.Count > 0) w.WriteLine($"{Range(aStart + 1, dels.Count)}d{bStart}");
            else w.WriteLine($"{aStart}a{Range(bStart + 1, ins.Count)}");
            foreach (var d in dels) w.WriteLine("< " + x[d.A]);
            if (dels.Count > 0 && ins.Count > 0) w.WriteLine("---");
            foreach (var n in ins) w.WriteLine("> " + y[n.B]);
            i = j;
        }
    }

    static void Unified(TextWriter w, List<(Op Op, int A, int B)> ops, List<string> x, List<string> y, string an, string bn, int context)
    {
        w.WriteLine($"--- {an}");
        w.WriteLine($"+++ {bn}");
        var changes = Enumerable.Range(0, ops.Count).Where(i => ops[i].Op != Op.Equal).ToList();
        for (int c = 0; c < changes.Count;)
        {
            int first = changes[c], last = first;
            while (c + 1 < changes.Count && changes[c + 1] - last <= 2 * context + 1) last = changes[++c];
            c++;
            int start = Math.Max(0, first - context), end = Math.Min(ops.Count, last + context + 1);
            var hunk = ops.GetRange(start, end - start);
            int aLen = hunk.Count(o => o.Op != Op.Insert), bLen = hunk.Count(o => o.Op != Op.Delete);
            int aStart = hunk.Min(o => o.A) + (aLen > 0 ? 1 : 0), bStart = hunk.Min(o => o.B) + (bLen > 0 ? 1 : 0);
            w.WriteLine($"@@ -{aStart},{aLen} +{bStart},{bLen} @@");
            foreach (var o in hunk)
                w.WriteLine(o.Op switch { Op.Equal => " " + x[o.A], Op.Delete => "-" + x[o.A], _ => "+" + y[o.B] });
        }
    }
}

/// <summary>chmod with octal (755) and symbolic (u+x, go-w, a=r, +x) modes, -R, -v and -c.</summary>
internal static class Chmod
{
    public static Task<int> Run(CommandContext ctx)
    {
        bool recursive = false, verbose = false, changes = false, quiet = false;
        string? spec = null;
        var files = new List<string>();
        foreach (var arg in ctx.Args)
        {
            if (arg is "-R" or "--recursive") recursive = true;
            else if (arg is "-v" or "--verbose") verbose = true;
            else if (arg is "-c" or "--changes") changes = true;
            else if (arg is "-f" or "--silent" or "--quiet") quiet = true;
            else if (arg.StartsWith("--") && arg.Length > 2) throw new UsageException(CommandSpec.Message("chmod", "unrecognizedOption", ("option", arg)));
            else if (arg.Length > 1 && arg[0] == '-' && arg.FirstOrDefault(c => !"-rwxXstugoa+=,01234567".Contains(c)) is var bad and not '\0')
                throw new UsageException(CommandSpec.Message("chmod", "invalidOption", ("option", bad.ToString())));
            else if (spec == null) spec = arg; // may look like an option, e.g. -x or -w
            else files.Add(arg);
        }
        if (spec == null || files.Count == 0) throw new UsageException(spec == null ? CommandSpec.Message("chmod", "missingOperand") : CommandSpec.Message("chmod", "missingOperandAfter", ("operand", spec)));
        int code = 0;
        void Apply(string display, string path)
        {
            var st = ctx.Fs.Stat(path);
            var mode = Parse(spec, st.Mode, st.IsDirectory, ctx.Fs.Umask);
            try { ctx.Fs.Chmod(path, mode); }
            catch (ShellException) { if (!quiet) ctx.Stderr.WriteLine("chmod: " + CommandSpec.Message("chmod", "cannotChangePermissions", ("file", display))); code = 1; return; }
            string Fmt(int m) => $"{Convert.ToString(m, 8).PadLeft(4, '0')} ({VirtualFileSystem.FormatMode(st.IsDirectory, m)[1..]})";
            if (mode != st.Mode && (verbose || changes)) ctx.Stdout.WriteLine($"mode of '{display}' changed from {Fmt(st.Mode)} to {Fmt(mode)}");
            else if (mode == st.Mode && verbose) ctx.Stdout.WriteLine($"mode of '{display}' retained as {Fmt(mode)}");
            if (recursive && st.IsDirectory && ctx.Fs.CanRead(path))
                foreach (var n in ctx.Fs.List(path))
                {
                    var shown = display.TrimEnd('/') + "/" + n;
                    // like chmod -R, links met on the way are left alone
                    if (!ctx.Fs.IsSymlink(VirtualFileSystem.Normalize(path, n))) Apply(shown, VirtualFileSystem.Normalize(path, n));
                    else if (verbose) ctx.Stdout.WriteLine($"neither symbolic link '{shown}' nor referent has been changed");
                }
        }
        foreach (var f in files)
        {
            var path = ctx.ResolvePath(f);
            if (!ctx.Fs.Exists(path)) { ctx.Stderr.WriteLine("chmod: " + CommandSpec.Message("chmod", "cannotAccess", ("file", f), ("reason", ctx.Fs.WhyMissing(path)))); code = 1; continue; }
            Apply(f, path);
        }
        return Task.FromResult(code);
    }

    /// <param name="umask">With no u, g, o or a, the bits the umask covers are left alone.</param>
    public static int Parse(string spec, int mode, bool isDir, int umask = 0)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(spec, "^[0-7]{1,4}$")) return Convert.ToInt32(spec, 8);
        foreach (var clause in spec.Split(','))
        {
            var m = System.Text.RegularExpressions.Regex.Match(clause, "^([ugoa]*)([-+=])([rwxXt]*)$");
            if (!m.Success) throw new ShellException(CommandSpec.Message("chmod", "invalidMode", ("mode", spec)));
            var who = m.Groups[1].Value is "" or "a" ? "ugo" : m.Groups[1].Value.Replace("a", "ugo");
            int bits = 0;
            bool sticky = false;
            foreach (var c in m.Groups[3].Value)
                switch (c)
                {
                    case 'r': bits |= 4; break;
                    case 'w': bits |= 2; break;
                    case 'x': bits |= 1; break;
                    case 'X': if (isDir || (mode & 0b001_001_001) != 0) bits |= 1; break;
                    case 't': sticky = true; break;
                }
            int mask = 0, clear = 0;
            foreach (var w in who)
            {
                int shift = w == 'u' ? 6 : w == 'g' ? 3 : 0;
                mask |= bits << shift;
                clear |= 7 << shift;
            }
            if (m.Groups[1].Value == "") mask &= ~umask;
            int stickyBit = sticky ? 0x200 : 0;
            mode = m.Groups[2].Value switch
            {
                "+" => mode | mask | stickyBit,
                "-" => mode & ~(mask | stickyBit),
                _ => (mode & ~(m.Groups[1].Value == "" ? clear & ~umask : clear)) | mask | stickyBit,
            };
        }
        return mode;
    }
}
