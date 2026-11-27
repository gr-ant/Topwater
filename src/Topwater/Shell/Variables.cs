// Shell: options, scalar variables and their attributes, arrays. See docs/architecture.md.
using System.Globalization;
using Topwater.Parsing;

namespace Topwater;

public sealed partial class Shell
{
    // ---------- options ----------

    /// <summary>[[ -v name ]], [[ -v a[k] ]]: whether the variable or element is set.</summary>
    internal bool IsVariableSet(string expr)
    {
        if (ArrayRef(expr) is { Keys: false, Tail: "" } r)
            return r.Sub is "@" or "*" ? GetArray(r.Name) is { Items.Count: > 0 } : GetElement(r.Name, r.Sub) != null;
        return Parser.IsName(expr) || expr.All(char.IsDigit) ? IsSet(expr) : false;
    }

    /// <summary>The set -o options this shell has, and whether each is on.</summary>
    internal IEnumerable<(string Name, bool On)> OptionList => [
        ("allexport", false), ("braceexpand", true), ("emacs", false), ("errexit", Errexit), ("errtrace", false), ("functrace", false),
        ("hashall", true), ("histexpand", false), ("history", false), ("ignoreeof", false), ("interactive-comments", true), ("keyword", false),
        ("monitor", false), ("noclobber", Noclobber), ("noexec", false), ("noglob", Noglob), ("nolog", false), ("notify", false),
        ("nounset", Nounset), ("onecmd", false), ("physical", false), ("pipefail", Pipefail), ("posix", false), ("privileged", false),
        ("verbose", false), ("vi", false), ("xtrace", Xtrace)];

    /// <summary>set -o NAME / set +o NAME; false for an unknown name.</summary>
    internal bool SetOption(string name, bool on)
    {
        switch (name)
        {
            case "errexit": Errexit = on; return true;
            case "nounset": Nounset = on; return true;
            case "pipefail": Pipefail = on; return true;
            case "xtrace": Xtrace = on; return true;
            case "noglob": Noglob = on; return true;
            case "noclobber": Noclobber = on; return true;
            default: return OptionList.Any(o => o.Name == name); // accepted, no effect here
        }
    }

    /// <summary>`local NAME[=VALUE]`: remembers the outer value and attributes so they are restored when the function returns.</summary>
    internal void DeclareLocal(string name, string? value)
    {
        if (_locals.Count == 0) throw new ShellException(CommandSpec.Diagnostics.OnlyInFunction);
        if (_locals.Peek().TryAdd(name, (Environment.TryGetValue(name, out var old) ? old : null, VarAttrs.GetValueOrDefault(name)))) VarAttrs.Remove(name);
        MarkShellVariable(name);
        Environment[name] = value ?? "";
    }

    /// <summary>$-: the single-letter options that are on.</summary>
    string OptionFlags => (Errexit ? "e" : "") + (Noglob ? "f" : "") + "h" + (Nounset ? "u" : "") + (Xtrace ? "x" : "") + "B" + (Noclobber ? "C" : "") + "c";

    // ---------- scalars ----------

    internal bool HasAttr(string name, char attr) => VarAttrs.Count > 0 && VarAttrs.TryGetValue(name, out var f) && f.Contains(attr);

    /// <summary>Whether a child bash (bash -c, a script) sees the variable: declared -x, or put in Environment by the host.</summary>
    internal bool IsExported(string name) => HasAttr(name, 'x') || Environment.ContainsKey(name) && !_unexported.Contains(name);

    /// <summary>Records that the shell (not the host) is about to create name, so it is not exported until `export`.</summary>
    internal void MarkShellVariable(string name)
    {
        if (!Environment.ContainsKey(name)) _unexported.Add(name);
    }

    internal void Unexport(string name)
    {
        SetAttrs(name, "", "x");
        _unexported.Add(name);
    }

    internal void SetAttrs(string name, string on, string off = "")
    {
        var f = VarAttrs.GetValueOrDefault(name, "");
        if (on.Contains('l')) f = f.Replace("u", "");
        if (on.Contains('u')) f = f.Replace("l", "");
        foreach (var c in on) if (!f.Contains(c)) f += c;
        foreach (var c in off) f = f.Replace(c.ToString(), "");
        if (f.Length == 0) VarAttrs.Remove(name); else VarAttrs[name] = f;
    }

    /// <summary>Follows namerefs (declare -n ref=name) to the variable they stand for.</summary>
    internal string Resolve(string name)
    {
        for (int i = 0; i < 10 && HasAttr(name, 'n') && Environment.TryGetValue(name, out var target) && target.Length > 0; i++) name = target;
        return name;
    }

    /// <summary>
    /// Assigns a variable the way bash does: through namerefs, refusing readonly ones, and applying -i (arithmetic),
    /// -l and -u. name[sub] sets an element; a plain assignment to an array sets element 0.
    /// </summary>
    internal void SetVar(string name, string value, bool append = false)
    {
        name = Resolve(name);
        if (ArrayRef(name) is { Keys: false, Tail: "" } r && r.Sub is not ("@" or "*"))
        {
            CheckWritable(r.Name);
            SetElement(r.Name, r.Sub, Converted(Resolve(r.Name), value, append ? GetElement(r.Name, r.Sub) : null));
            return;
        }
        if (!Parser.IsName(name)) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.NotValidIdentifier, ("name", name)));
        CheckWritable(name);
        if (name == "SECONDS" && long.TryParse(value, out var seconds)) { _secondsBase = Now.AddSeconds(-seconds); return; }
        if (name == "RANDOM") { _random = (ulong)(long.TryParse(value, out var seed) ? seed : 0); return; }
        if (name == "PATH") Hashed.Clear(); // even to the same value
        var array = Arrays.ContainsKey(name);
        value = Converted(name, value, !append ? null : array ? GetElement(name, "0") : Environment.GetValueOrDefault(name));
        if (array) SetElement(name, "0", value);
        else
        {
            ChargeVariable(name, value.Length, Environment.GetValueOrDefault(name)?.Length ?? 0);
            MarkShellVariable(name);
            Environment[name] = value;
        }
    }

    string Converted(string name, string value, string? old)
    {
        if (VarAttrs.Count == 0 || !VarAttrs.TryGetValue(name, out var f)) return old + value;
        if (f.Contains('i')) return (Arith.Eval(this, value) + (old is { Length: > 0 } ? Arith.Eval(this, old) : 0)).ToString(CultureInfo.InvariantCulture);
        value = old + value;
        return f.Contains('l') ? value.ToLowerInvariant() : f.Contains('u') ? value.ToUpperInvariant() : value;
    }

    void CheckWritable(string name)
    {
        if (HasAttr(name, 'r')) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.ReadonlyVariable, ("name", name)));
    }

    static readonly string[] BashVersInfo = ["5", "2", "21", "1", "release", "x86_64-pc-linux-gnu"];

    /// <summary>The array called name (through namerefs), including the shell's own FUNCNAME, PIPESTATUS and BASH_VERSINFO.</summary>
    internal ShellArray? GetArray(string name)
    {
        name = Resolve(name);
        if (Arrays.TryGetValue(name, out var a)) return a;
        IEnumerable<string>? items = name switch
        {
            "FUNCNAME" when _funcNames.Count > 0 => Enumerable.Reverse(_funcNames),
            "BASH_SOURCE" when _sources.Count > 0 => Enumerable.Reverse(_sources),
            "PIPESTATUS" => _pipeStatus.Select(c => c.ToString(CultureInfo.InvariantCulture)),
            "BASH_VERSINFO" => BashVersInfo,
            _ => null,
        };
        if (items == null) return null;
        var built = new ShellArray(false);
        foreach (var item in items) built.Set(built.Next.ToString(CultureInfo.InvariantCulture), item);
        return built;
    }

    /// <summary>Shell variables bash sets itself; they are not in the environment `env` prints.</summary>
    string? ShellVariable(string name) => name switch
    {
        "IFS" => _ifsUnset ? null : " \t\n",
        "OPTIND" => "1",
        "BASH" => "/usr/bin/bash",
        "BASH_VERSION" => "5.2.21(1)-release",
        "UID" or "EUID" => Options.User == "root" ? "0" : "1000",
        "PPID" => "4241",
        "BASHPID" => "4242",
        "SHLVL" => "1",
        "OSTYPE" => "linux-gnu",
        "MACHTYPE" => "x86_64-pc-linux-gnu",
        "HOSTTYPE" => "x86_64",
        "LINENO" => _lineno.ToString(CultureInfo.InvariantCulture),
        "SECONDS" => ((long)(Now - _secondsBase).TotalSeconds).ToString(CultureInfo.InvariantCulture),
        "EPOCHSECONDS" => new DateTimeOffset(Now).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
        "EPOCHREALTIME" => (new DateTimeOffset(Now).ToUnixTimeMilliseconds() / 1000.0).ToString("0.000000", CultureInfo.InvariantCulture),
        _ => null,
    };

    internal string GetVariable(string name)
    {
        if (name.Length > 0 && char.IsDigit(name[0]) && int.TryParse(name, out var n))
            return n == 0 ? _arg0 : n <= Positional.Count ? Positional[n - 1] : "";
        return name switch
        {
            "?" => LastExitCode.ToString(CultureInfo.InvariantCulture),
            "$" => "4242",
            "!" => _lastBg == 0 ? "" : _lastBg.ToString(CultureInfo.InvariantCulture),
            "#" => Positional.Count.ToString(CultureInfo.InvariantCulture),
            "@" => string.Join(' ', Positional),
            "*" => string.Join(IfsJoiner, Positional),
            "-" => OptionFlags,
            "_" => _lastArg,
            "PWD" => _cwd,
            "RANDOM" => NextRandom(32768).ToString(CultureInfo.InvariantCulture),
            _ => LookUp(name) ?? "",
        };
    }

    /// <summary>A named variable's value (element 0 of an array), or null when it is unset.</summary>
    string? LookUp(string name)
    {
        name = Resolve(name);
        if (GetArray(name) is { } array) return array.Get("0");
        return Environment.TryGetValue(name, out var v) ? v : ShellVariable(name);
    }

    /// <summary>The IFS that splitting uses: the default when it is unset.</summary>
    internal string Ifs => Environment.GetValueOrDefault("IFS", " \t\n");

    /// <summary>"$*" and "${a[*]}" join with the first character of IFS (a space when IFS is unset).</summary>
    string IfsJoiner => Environment.TryGetValue("IFS", out var ifs) ? (ifs.Length > 0 ? ifs[..1] : "") : " ";

    bool IsSet(string name) =>
        name is "?" or "$" or "#" or "@" or "*" or "-" or "_" or "PWD" or "RANDOM" or "0"
        || (char.IsDigit(name[0]) && int.TryParse(name, out var n) && n <= Positional.Count)
        || name == "!" && _lastBg != 0
        || LookUp(name) != null;

    // ---------- arrays ----------

    /// <summary>The array to change: created if missing, copied first if a saved state still shares it.</summary>
    internal ShellArray Writable(string name, bool assoc = false)
    {
        name = Resolve(name);
        CheckWritable(name);
        if (!Arrays.TryGetValue(name, out var a))
        {
            a = new ShellArray(assoc) { Epoch = _epoch };
            if (Environment.Remove(name, out var scalar)) a.Set("0", scalar); // a plain variable becomes element 0
            return Arrays[name] = a;
        }
        return a.Epoch == _epoch ? a : Arrays[name] = a.Clone(_epoch);
    }

    /// <summary>`local arr` / `declare -A m`: a fresh array, restored to the caller's when the function returns.</summary>
    internal void DeclareArray(string name, bool assoc, bool local)
    {
        CheckWritable(name);
        if (local && _localArrays.Count > 0) _localArrays.Peek().TryAdd(name, Arrays.GetValueOrDefault(name));
        else if (Arrays.TryGetValue(name, out var old) && old.Assoc == assoc) return;
        if (local && _locals.Count > 0) DeclareLocal(name, null);
        Environment.Remove(name);
        Arrays[name] = new ShellArray(assoc) { Epoch = _epoch };
    }

    /// <summary>The storage key for a subscript: the text itself for an associative array, else its arithmetic value.</summary>
    internal string ArrayKey(ShellArray? array, string subscript, string name = "")
    {
        if (array is { Assoc: true }) return subscript;
        long i;
        try { i = Arith.Eval(this, subscript); }
        catch (ShellException e) when (e is not FatalError) { throw new FatalError(e.Message, 1); }
        if (i < 0) i += array?.Next ?? 1;
        return i >= 0 ? i.ToString(CultureInfo.InvariantCulture) : throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.BadArraySubscript, ("name", name)));
    }

    /// <summary>One element, or null if unset. A plain variable answers as element 0.</summary>
    internal string? GetElement(string name, string subscript)
    {
        name = Resolve(name);
        if (GetArray(name) is { } a) return a.Get(ArrayKey(a, subscript, name));
        return Environment.TryGetValue(name, out var scalar) && ArrayKey(null, subscript, name) == "0" ? scalar : null;
    }

    internal void SetElement(string name, string subscript, string value)
    {
        var a = Writable(name);
        var key = ArrayKey(a, subscript, name);
        ChargeVariable(name, key.Length + value.Length, a.Get(key) is { } old ? key.Length + old.Length : 0);
        a.Set(key, value);
    }

    /// <summary>unset NAME or unset 'NAME[sub]'; -n (<paramref name="nameref"/>) unsets a nameref itself, not what it names.</summary>
    internal void Unset(string target, bool nameref = false)
    {
        if (!nameref) target = Resolve(target);
        if (ArrayRef(target) is { Keys: false, Tail: "" } r && Arrays.ContainsKey(r.Name) && r.Sub is not ("@" or "*"))
        {
            var a = Writable(r.Name);
            a.Items.Remove(ArrayKey(a, Unquote(r.Sub), r.Name));
            return;
        }
        var name = ArrayRef(target)?.Name ?? target;
        if (HasAttr(name, 'r')) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.CannotUnsetReadonly, ("name", name)));
        Environment.Remove(name);
        Arrays.Remove(name);
        VarAttrs.Remove(name);
        _unexported.Remove(name);
        if (name == "IFS") _ifsUnset = true;
    }

    /// <summary>The elements ${name[@]...} stands for (keys for ${!name[@]}); null if expr is not a whole-array expansion.</summary>
    async Task<List<string>?> ArrayElementsAsync(string expr, Io io, CancellationToken ct, bool quoted = false)
    {
        if (ArrayRef(expr) is not { Sub: "@" or "*" } r) return null;
        var a = GetArray(r.Name);
        var name = Resolve(r.Name);
        List<string> items = a == null ? (Environment.TryGetValue(name, out var scalar) ? [r.Keys ? "0" : scalar] : [])
            : r.Keys ? [.. a.Items.Keys] : [.. a.Items.Values];
        if (r.Keys || r.Tail.Length == 0) return items;
        if (DefaultOp(r.Tail) is var (op, word))
        {
            // ${a[@]:-word}: the array as a whole is tested; empty means no elements (or, with ':', nothing but empty text)
            bool missing = items.Count == 0 || op[0] == ':' && string.Join(r.Sub == "*" ? IfsJoiner : " ", items).Length == 0;
            if (op.EndsWith('?') && missing) throw Fatal(io, $"{r.Name}[{r.Sub}]: {(word.Length > 0 ? await ParamWordAsync(word, quoted, io, ct) : "parameter null or not set")}", 127);
            if (op.EndsWith('=') && missing) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.BadArraySubscript, ("name", $"{r.Name}[{r.Sub}]")));
            if (op.EndsWith('-')) return missing ? [await ParamWordAsync(word, quoted, io, ct)] : items;
            if (op.EndsWith('+')) return missing ? [] : [await ParamWordAsync(word, quoted, io, ct)];
            return items;
        }
        if (r.Tail[0] == ':')
        {
            var spec = r.Tail[1..].Split(':', 2);
            long from = await EvalArithAsync(spec[0], io, ct);
            long count = spec.Length == 1 ? long.MaxValue : await EvalArithAsync(spec[1], io, ct);
            if (count < 0) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.SubstringNegative, ("expression", spec[1].Trim())));
            // an indexed array is sliced by index, so a sparse array skips its gaps: offsets count from the first index >= from
            if (a is { Assoc: false } && !r.Keys)
            {
                var keys = a.Items.Keys.Select(long.Parse).ToList();
                if (from < 0) from += keys.Count == 0 ? 0 : keys[^1] + 1;
                if (from < 0) return [];
                int first = keys.FindIndex(k => k >= from);
                return first < 0 ? [] : items.Skip(first).Take((int)Math.Min(count, int.MaxValue)).ToList();
            }
            if (from < 0) from += items.Count;
            if (from < 0 || from > items.Count) return [];
            return items.Skip((int)from).Take((int)Math.Min(count, int.MaxValue)).ToList();
        }
        // any other operator applies to each element: ${files[@]%.txt}, ${names[@]^^}
        var result = new List<string>();
        foreach (var item in items) result.Add(await ElementOpAsync(item, r.Tail, io, ct, quoted));
        return result;
    }

    const string ScratchVariable = "__topwater_element";

    /// <summary>Applies a ${...} operator to one value by expanding it through a scratch variable.</summary>
    async Task<string> ElementOpAsync(string? value, string rest, Io io, CancellationToken ct, bool quoted = false)
    {
        if (rest.Length == 0) return value ?? "";
        if (value == null) Environment.Remove(ScratchVariable); else Environment[ScratchVariable] = value;
        try { return await ExpandParameterAsync(ScratchVariable + rest, io, ct, quoted); }
        finally { Environment.Remove(ScratchVariable); }
    }

    /// <summary>`unset 'a[${#a[@]}-1]'`: name[sub] with the subscript's expansions done (the subscript stays text otherwise).</summary>
    internal async Task<string> ExpandSubscriptAsync(string target, Io io, CancellationToken ct) =>
        ArrayRef(target) is { Keys: false, Tail: "" } r && r.Sub.IndexOfAny(['$', '`']) >= 0 ? $"{r.Name}[{await SubscriptAsync(r.Sub, io, ct)}]" : target;

    async Task<string> SubscriptAsync(string sub, Io io, CancellationToken ct) =>
        sub.IndexOfAny(['$', '`', '"', '\'']) < 0 ? sub : await ExpandSingleAsync(new Word(new Lexer(Unquote(sub)).LexQuoted(null)), io, ct);

    async Task AssignArrayAsync(Assignment a, Io io, CancellationToken ct)
    {
        var name = Resolve(a.Name);
        if (a.Elements == null)
        {
            var key = await ExpandSingleAsync(a.Index!, io, ct);
            var value = await ExpandAssignmentAsync(a.Value, io, ct);
            if (Xtrace) io.Err.WriteLine($"+ {name}[{key}]{(a.Append ? "+=" : "=")}{TraceQuote(value)}");
            SetVar($"{name}[{key}]", value, a.Append);
            return;
        }
        ShellArray target;
        if (a.Append) target = Writable(name);
        else
        {
            CheckWritable(name);
            target = new ShellArray(Arrays.TryGetValue(name, out var old) && old.Assoc) { Epoch = _epoch };
            Environment.Remove(name);
        }
        foreach (var w in Parser.ParseWords(a.Elements))
        {
            // [key]=value sets that element; anything else is appended, after the usual splitting and globbing
            if (w.Parts is [LiteralPart { Quoted: false, Text: ['[', ..] }, ..] && Parser.SplitSubscript(w, 1) is var (index, element, _))
                target.Set(ArrayKey(target, await ExpandSingleAsync(index, io, ct)), await ExpandSingleAsync(element, io, ct));
            else
                foreach (var field in await ExpandWordsAsync([w], io, ct)) target.Set(target.Next.ToString(CultureInfo.InvariantCulture), field);
        }
        Arrays[name] = target;
    }

    /// <summary>
    /// `local a=(x y)`, `declare -A m`, `declare -a list=()`: the array arguments are handled here, because a built-in
    /// only receives expanded text. Returns the words left for the built-in, or null if none were array declarations.
    /// </summary>
    async Task<List<Word>?> DeclareArraysAsync(List<Word> words, Io io, CancellationToken ct)
    {
        var flags = string.Concat(words.Skip(1).Select(w => w.UnquotedLiteral).Where(t => t is ['-', ..]));
        if (flags.Contains('p') || flags.Contains('f') || flags.Contains('F')) return null;
        bool local = words[0].Is("local") || InFunction && !flags.Contains('g');
        bool assoc = flags.Contains('A'), any = false;
        var attrs = string.Concat(flags.Where(c => c is 'r' or 'x')) + (words[0].Is("readonly") ? "r" : "");
        var keep = new List<Word> { words[0] };
        foreach (var w in words.Skip(1))
        {
            if (w.UnquotedLiteral is ['-', ..]) continue;
            var assignment = Parser.TryAssignment(w);
            if (assignment is { Elements: not null })
            {
                DeclareArray(assignment.Name, assoc, local);
                await AssignArrayAsync(assignment, io, ct);
            }
            else if (assignment == null && (assoc || flags.Contains('a')) && w.UnquotedLiteral is { } name && Parser.IsName(name))
                DeclareArray(name, assoc, local);
            else { keep.Add(w); continue; }
            if (attrs.Length > 0) SetAttrs(assignment?.Name ?? w.UnquotedLiteral!, attrs);
            any = true;
        }
        return any ? keep : null;
    }

    /// <summary>Splits name[sub]rest (or !name[sub] for the keys); null when the text is not that shape.</summary>
    internal static (bool Keys, string Name, string Sub, string Tail)? ArrayRef(string expr)
    {
        int start = expr.StartsWith('!') ? 1 : 0, open = expr.IndexOf('[');
        if (open <= start || !Parser.IsName(expr[start..open])) return null;
        int depth = 0, k = open;
        for (; k < expr.Length; k++)
            if (expr[k] == '[') depth++;
            else if (expr[k] == ']' && --depth == 0) break;
        return k < expr.Length ? (start == 1, expr[start..open], expr[(open + 1)..k], expr[(k + 1)..]) : null;
    }

    static string Unquote(string s) => s.Length >= 2 && s[0] is '"' or '\'' && s[^1] == s[0] ? s[1..^1] : s;
}
