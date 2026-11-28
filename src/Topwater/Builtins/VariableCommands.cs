// Variable builtins: export, unset, env, alias, declare and its family, read, mapfile, printenv. See docs/architecture.md.

namespace Topwater;

internal static partial class Builtins
{
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

    static void RegisterVariableCommands(Shell shell)
    {
        shell.Register("export", ctx =>
        {
            var sh = ctx.Shell;
            var flags = string.Concat(ctx.Args.TakeWhile(a => a.StartsWith('-')));
            bool remove = flags.Contains('n');
            var args = ctx.Args.SkipWhile(a => a.StartsWith('-')).ToList();
            if (flags.Contains('f'))
            {
                // export -f: functions a child bash also gets
                foreach (var f in args) if (remove) sh.ExportedFunctions.Remove(f); else if (sh.Functions.ContainsKey(f)) sh.ExportedFunctions.Add(f);
                return 0;
            }
            if (args.Count == 0)
            {
                foreach (var k in sh.Environment.Keys.Concat(sh.Arrays.Keys).Distinct().Order(StringComparer.Ordinal))
                    if (sh.IsExported(k) && DeclareLine(sh, k) is { } line) ctx.Stdout.WriteLine(line);
                return 0;
            }
            int code = 0;
            foreach (var a in args)
            {
                int eq = a.IndexOf('=');
                bool append = eq > 0 && a[eq - 1] == '+';
                var name = eq > 0 ? a[..(append ? eq - 1 : eq)] : a;
                try
                {
                    if (!Topwater.Parsing.Parser.IsName(name)) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.NotValidIdentifier, ("name", a)));
                    if (eq > 0) sh.SetVar(name, a[(eq + 1)..], append);
                    else if (!sh.Arrays.ContainsKey(sh.Resolve(name)) && !sh.Environment.ContainsKey(sh.Resolve(name))) { sh.MarkShellVariable(sh.Resolve(name)); sh.Environment[sh.Resolve(name)] = ""; }
                    if (remove) sh.Unexport(sh.Resolve(name)); else sh.SetAttrs(sh.Resolve(name), "x");
                }
                catch (ShellException e) { ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}export: {e.Message}"); code = 1; }
            }
            return code;
        });

        shell.Register("unset", async ctx =>
        {
            var flags = string.Concat(ctx.Args.TakeWhile(a => a.StartsWith('-')));
            int code = 0;
            foreach (var arg in ctx.Args.SkipWhile(a => a.StartsWith('-')))
            {
                try
                {
                    if (!flags.Contains('f') && !Topwater.Parsing.Parser.IsName(arg.Split('[')[0]))
                    {
                        if (flags.Contains('v')) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.NotValidIdentifier, ("name", arg)));
                        continue; // plain unset passes over what can be neither a variable nor a function, silently
                    }
                    var a = await ctx.Shell.ExpandSubscriptAsync(arg, ctx.Io, ctx.CancellationToken);
                    // with neither -v nor -f, a name that is no variable but a function unsets the function
                    if (flags.Contains('f') || !flags.Contains('v') && !ctx.Shell.IsVariableSet(a) && ctx.Shell.Functions.ContainsKey(a)) ctx.Shell.Functions.Remove(a);
                    else ctx.Shell.Unset(a, nameref: flags.Contains('n'));
                }
                catch (ShellException e) { ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}unset: {e.Message}"); code = 1; }
            }
            return code;
        });

        shell.Register("env", async ctx =>
        {
            // env [-i] [-u NAME] [-C DIR] [-0] [NAME=VALUE]... [COMMAND [ARG]...]: the exported variables, or COMMAND run with changed ones
            var sh = ctx.Shell;
            var args = ctx.Args.ToList();
            var sets = new List<(string Name, string Value)>();
            var unsets = new List<string>();
            bool clear = false, nul = false;
            string? dir = null;
            string Value(char opt, string rest)
            {
                if (rest.Length > 0) return rest;
                if (args.Count > 1) { args.RemoveAt(0); return args[0]; }
                throw new UsageException(CommandSpec.Format(CommandSpec.Getopt.MissingShortArgument, ("option", opt.ToString())));
            }
            // options stop at the first assignment or command, as getopt does with a leading +
            while (args.Count > 0 && args[0].Length > 1 && args[0][0] == '-')
            {
                var a = args[0];
                if (a == "--") { args.RemoveAt(0); break; }
                if (a.StartsWith("--"))
                {
                    var (name, inline) = a.IndexOf('=') is var eq and > 0 ? (a[2..eq], a[(eq + 1)..]) : (a[2..], null);
                    string LongValue() => inline ?? (args.Count > 1 ? args[1] : throw new UsageException(CommandSpec.Format(CommandSpec.Getopt.MissingLongArgument, ("option", name))));
                    bool takesValue = false;
                    switch (name)
                    {
                        case "ignore-environment": clear = true; break;
                        case "null": nul = true; break;
                        case "unset": unsets.Add(LongValue()); takesValue = true; break;
                        case "chdir": dir = LongValue(); takesValue = true; break;
                        default: throw new UsageException(CommandSpec.Format(CommandSpec.Getopt.UnrecognizedOption, ("option", a)));
                    }
                    args.RemoveAt(0);
                    if (takesValue && inline == null) args.RemoveAt(0);
                    continue;
                }
                for (int k = 1; k < a.Length; k++)
                {
                    char c = a[k];
                    if (c == 'i') clear = true;
                    else if (c == '0') nul = true;
                    else if (c == 'u') { unsets.Add(Value(c, a[(k + 1)..])); break; }
                    else if (c == 'C') { dir = Value(c, a[(k + 1)..]); break; }
                    else throw new UsageException(CommandSpec.Format(CommandSpec.Getopt.InvalidOption, ("option", c.ToString())));
                }
                args.RemoveAt(0);
            }
            if (args.Count > 0 && args[0] == "-") { clear = true; args.RemoveAt(0); }
            while (args.Count > 0 && args[0].IndexOf('=') is var eq and > 0) { sets.Add((args[0][..eq], args[0][(eq + 1)..])); args.RemoveAt(0); }
            void Print() { foreach (var (k, v) in sh.Environment.Where(e => sh.IsExported(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal)) ctx.Stdout.Write($"{k}={v}{(nul ? '\0' : '\n')}"); }
            if (args.Count == 0 && sets.Count == 0 && !clear && unsets.Count == 0) { Print(); return 0; }
            var (savedEnv, savedAttrs, savedCwd) = (sh.Environment, sh.VarAttrs, sh.Cwd);
            sh.Environment = clear ? new(StringComparer.Ordinal)
                : savedEnv.Where(e => sh.IsExported(e.Key) && !unsets.Contains(e.Key)).ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
            sh.VarAttrs = sh.Environment.Keys.ToDictionary(k => k, _ => "x", StringComparer.Ordinal);
            foreach (var (k, v) in sets) { sh.Environment[k] = v; sh.VarAttrs[k] = "x"; }
            try
            {
                if (args.Count == 0) { Print(); return 0; }
                if (dir != null)
                {
                    var abs = ctx.ResolvePath(dir);
                    var why = !ctx.Fs.Exists(abs) ? ctx.Fs.WhyMissing(abs) : !ctx.Fs.IsDirectory(abs) ? CommandSpec.Errno.ENOTDIR : !ctx.Fs.CanExecute(abs) ? CommandSpec.Errno.EACCES : null;
                    if (why != null)
                    {
                        ctx.Stderr.WriteLine("env: " + CommandSpec.Message("env", "cannotChdir", ("dir", dir), ("errno", why)));
                        return 125;
                    }
                    sh.Cwd = abs;
                }
                var cmd = args[0];
                if (ExecFailure(ctx, cmd, out var path) is { } error)
                {
                    ctx.Stderr.WriteLine("env: " + CommandSpec.Message("env", "runFailed", ("command", cmd), ("errno", error)));
                    return error == CommandSpec.Errno.EACCES ? 126 : 127;
                }
                if (path != null && !cmd.Contains('/') && !sh.Environment.ContainsKey("PATH")) args[0] = path;
                return await sh.InvokeAsync(args, ctx.Io, ctx.CancellationToken, skipFunctions: true);
            }
            finally
            {
                (sh.Environment, sh.VarAttrs) = (savedEnv, savedAttrs);
                if (sh.Cwd != savedCwd && ctx.Fs.IsDirectory(savedCwd)) sh.Cwd = savedCwd;
            }
        });

        shell.Register("alias", ctx =>
        {
            int code = 0;
            if (ctx.Args.Count == 0)
                foreach (var (k, v) in ctx.Shell.Aliases.OrderBy(e => e.Key, StringComparer.Ordinal)) ctx.Stdout.WriteLine($"alias {k}='{v}'");
            foreach (var a in ctx.Args)
            {
                int eq = a.IndexOf('=');
                if (eq > 0)
                {
                    var (name, value) = (a[..eq], a[(eq + 1)..]);
                    ctx.Shell.ChargeVariable($"alias {name}", name.Length + value.Length, ctx.Shell.Aliases.TryGetValue(name, out var old) ? name.Length + old.Length : 0);
                    ctx.Shell.Aliases[name] = value;
                }
                else if (ctx.Shell.Aliases.TryGetValue(a, out var v)) ctx.Stdout.WriteLine($"alias {a}='{v}'");
                else { ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}alias: " + CommandSpec.Message("alias", "notFound", ("name", a))); code = 1; }
            }
            return code;
        });

        shell.Register("unalias", ctx =>
        {
            var args = ctx.Args.ToList();
            bool all = false;
            for (; args.Count > 0 && args[0].Length > 1 && args[0][0] == '-'; args.RemoveAt(0))
            {
                if (args[0] == "--") { args.RemoveAt(0); break; }
                if (args[0] != "-a")
                {
                    ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}unalias: " + CommandSpec.Message("unalias", "builtinInvalidOption", ("option", args[0][..2])));
                    ctx.Stderr.WriteLine("unalias: " + CommandSpec.Message("unalias", "usage"));
                    return 2;
                }
                all = true;
            }
            if (all) { ctx.Shell.Aliases.Clear(); return 0; }
            if (args.Count == 0) { ctx.Stderr.WriteLine("unalias: " + CommandSpec.Message("unalias", "usage")); return 2; }
            int code = 0;
            foreach (var a in args)
                if (!ctx.Shell.Aliases.Remove(a)) { ctx.Stderr.WriteLine($"{ctx.Shell.ErrPrefix}unalias: " + CommandSpec.Message("unalias", "notFound", ("name", a))); code = 1; }
            return code;
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
    }
}
