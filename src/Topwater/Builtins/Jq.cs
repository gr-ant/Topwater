using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Topwater;

/// <summary>
/// A small jq: paths (.a.b, .[0], .[], ."key"), pipes, select(), map(), [ ... ], comparisons, and keys/length/type/not/add.
/// shortcut: no object construction, variables or string interpolation; add them if agents need them.
/// </summary>
internal static class Jq
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions Compact = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static void Register(Shell shell) => shell.Register("jq", ctx =>
    {
        var a = Args.Parse(ctx.Args, "rcnsSj", longs: "raw-output=r,compact-output=c,null-input=n,slurp=s,sort-keys=S,join-output=j");
        var filter = a.Positional.FirstOrDefault() ?? ".";
        int code = 0;
        var texts = Builtins.Inputs(ctx, a.Positional.Skip(1).ToList(), () => code = 2).Select(i => i.Text).ToList();

        var inputs = new List<JsonNode?>();
        if (a.Has('n')) inputs.Add(null);
        else
            foreach (var text in texts)
            {
                if (text.Trim().Length == 0) continue;
                try { inputs.Add(JsonNode.Parse(text)); }
                catch (JsonException)
                {
                    // JSON lines
                    foreach (var line in Builtins.SplitLines(text).Where(l => l.Trim().Length > 0))
                    {
                        try { inputs.Add(JsonNode.Parse(line)); }
                        catch (JsonException e) { throw new ShellException($"error: cannot parse input: {e.Message}", 2); }
                    }
                }
            }
        if (a.Has('s')) inputs = [new JsonArray(inputs.Select(n => n?.DeepClone()).ToArray())];

        var opts = a.Has('c') || a.Has('j') ? Compact : Pretty;
        foreach (var input in inputs)
            foreach (var result in Eval(filter, input))
            {
                var outNode = a.Has('S') ? SortKeys(result) : result;
                bool raw = (a.Has('r') || a.Has('j')) && outNode is JsonValue v && v.TryGetValue<string>(out _);
                ctx.Stdout.Write(raw ? outNode!.GetValue<string>() : outNode?.ToJsonString(opts) ?? "null");
                if (!a.Has('j')) ctx.Stdout.Write('\n');
            }
        return code;
    }, "Command-line JSON processor (subset)");

    static JsonNode? SortKeys(JsonNode? n) => n switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => KeyValuePair.Create(p.Key, SortKeys(p.Value?.DeepClone())))),
        JsonArray arr => new JsonArray(arr.Select(x => SortKeys(x?.DeepClone())).ToArray()),
        _ => n,
    };

    static ShellException Error(string msg) => new("error: " + msg, 5);

    static string TypeOf(JsonNode? n) => n switch
    {
        null => "null",
        JsonObject => "object",
        JsonArray => "array",
        JsonValue v => v.GetValueKind() switch { JsonValueKind.String => "string", JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean", _ => "null" },
        _ => "null",
    };

    static bool Truthy(JsonNode? n) => n != null && !(n is JsonValue v && v.GetValueKind() == JsonValueKind.False);

    /// <summary>Splits on <paramref name="sep"/> occurrences outside brackets and strings.</summary>
    static List<string> SplitTop(string s, string sep)
    {
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '"') { for (i++; i < s.Length && s[i] != '"'; i++) if (s[i] == '\\') i++; continue; }
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (depth == 0 && string.CompareOrdinal(s, i, sep, 0, sep.Length) == 0)
            {
                // don't split "|" inside "||" etc. and don't treat "==" as two "="
                parts.Add(s[start..i]);
                i += sep.Length - 1;
                start = i + 1;
            }
        }
        parts.Add(s[start..]);
        return parts;
    }

    static IEnumerable<JsonNode?> Eval(string f, JsonNode? input)
    {
        f = f.Trim();
        var pipe = SplitTop(f, "|");
        if (pipe.Count > 1)
        {
            IEnumerable<JsonNode?> results = [input];
            foreach (var stage in pipe) results = results.SelectMany(r => Eval(stage, r)).ToList();
            return results;
        }
        foreach (var op in new[] { " and ", " or " })
        {
            var p = SplitTop(f, op);
            if (p.Count > 1)
            {
                var l = Eval(p[0], input).Any(Truthy);
                var r = Eval(string.Join(op, p.Skip(1)), input).Any(Truthy);
                return [JsonValue.Create(op == " and " ? l && r : l || r)];
            }
        }
        foreach (var op in new[] { "==", "!=", ">=", "<=", ">", "<" })
        {
            var p = SplitTop(f, op);
            if (p.Count == 2)
                return Eval(p[0], input).SelectMany(l => Eval(p[1], input).Select(r => (JsonNode?)JsonValue.Create(Compare(l, r, op)))).ToList();
        }
        return EvalTerm(f, input);
    }

    static bool Compare(JsonNode? l, JsonNode? r, string op)
    {
        int cmp;
        if (TypeOf(l) == "number" && TypeOf(r) == "number") cmp = l!.GetValue<double>().CompareTo(r!.GetValue<double>());
        else if (TypeOf(l) == "string" && TypeOf(r) == "string") cmp = string.CompareOrdinal(l!.GetValue<string>(), r!.GetValue<string>());
        else cmp = (l?.ToJsonString() ?? "null") == (r?.ToJsonString() ?? "null") ? 0 : string.CompareOrdinal(TypeOf(l), TypeOf(r)) is var c && c != 0 ? c : 1;
        return op switch { "==" => cmp == 0, "!=" => cmp != 0, ">" => cmp > 0, "<" => cmp < 0, ">=" => cmp >= 0, _ => cmp <= 0 };
    }

    static IEnumerable<JsonNode?> EvalTerm(string f, JsonNode? input)
    {
        if (f.StartsWith('(') && f.EndsWith(')')) return Eval(f[1..^1], input);
        if (f.StartsWith('[') && f.EndsWith(']'))
            return [new JsonArray(Eval(f[1..^1], input).Select(n => n?.DeepClone()).ToArray())];
        if (f.StartsWith("select(") && f.EndsWith(')'))
            return Eval(f[7..^1], input).Any(Truthy) ? [input] : [];
        if (f.StartsWith("map(") && f.EndsWith(')'))
        {
            if (input is not JsonArray arr) throw Error($"Cannot iterate over {TypeOf(input)}");
            return [new JsonArray(arr.SelectMany(x => Eval(f[4..^1], x)).Select(n => n?.DeepClone()).ToArray())];
        }
        if (f.StartsWith("has(") && f.EndsWith(')'))
        {
            var key = Eval(f[4..^1], input).FirstOrDefault();
            return [JsonValue.Create(input is JsonObject o ? o.ContainsKey(key?.GetValue<string>() ?? "") : input is JsonArray ar && key?.GetValue<int>() < ar.Count)];
        }
        switch (f)
        {
            case "": throw Error("empty filter");
            case "keys":
                return input switch
                {
                    JsonObject o => [new JsonArray(o.Select(p => p.Key).Order(StringComparer.Ordinal).Select(k => (JsonNode?)JsonValue.Create(k)).ToArray())],
                    JsonArray arr => [new JsonArray(Enumerable.Range(0, arr.Count).Select(i => (JsonNode?)JsonValue.Create(i)).ToArray())],
                    _ => throw Error($"{TypeOf(input)} has no keys"),
                };
            case "length":
                return [JsonValue.Create(input switch
                {
                    null => 0,
                    JsonObject o => o.Count,
                    JsonArray arr => arr.Count,
                    JsonValue v when TypeOf(v) == "string" => v.GetValue<string>().Length,
                    JsonValue v when TypeOf(v) == "number" => Math.Abs(v.GetValue<double>()),
                    _ => throw Error($"{TypeOf(input)} has no length"),
                })];
            case "type": return [JsonValue.Create(TypeOf(input))];
            case "not": return [JsonValue.Create(!Truthy(input))];
            case "add":
                if (input is not JsonArray items) throw Error($"Cannot add {TypeOf(input)}");
                if (items.Count == 0) return [null];
                if (items.All(i => TypeOf(i) == "number")) return [JsonValue.Create(items.Sum(i => i!.GetValue<double>()))];
                if (items.All(i => TypeOf(i) == "string")) return [JsonValue.Create(string.Concat(items.Select(i => i!.GetValue<string>())))];
                if (items.All(i => i is JsonArray)) return [new JsonArray(items.SelectMany(i => i!.AsArray()).Select(n => n?.DeepClone()).ToArray())];
                throw Error("cannot add mixed types");
            case "true": return [JsonValue.Create(true)];
            case "false": return [JsonValue.Create(false)];
            case "null": return [null];
        }
        if (f.StartsWith('"')) return [JsonNode.Parse(f)];
        if (double.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return [JsonNode.Parse(f)];
        if (f.StartsWith('.')) return EvalPath(f, input);
        throw Error($"unsupported filter '{f}' (supported: paths, |, select, map, has, [..], keys, length, type, not, add, comparisons)");
    }

    static IEnumerable<JsonNode?> EvalPath(string f, JsonNode? input)
    {
        IEnumerable<JsonNode?> cur = [input];
        int i = 1; // skip leading '.'
        while (i < f.Length)
        {
            Func<JsonNode?, IEnumerable<JsonNode?>> step;
            if (f[i] == '.') { i++; continue; }
            if (f[i] == '[')
            {
                int close = f.IndexOf(']', i);
                if (close < 0) throw Error("unterminated [");
                var inner = f[(i + 1)..close].Trim();
                i = close + 1;
                if (inner.Length == 0) step = Iterate;
                else if (inner.StartsWith('"')) { var key = JsonNode.Parse(inner)!.GetValue<string>(); step = n => [Field(n, key)]; }
                else if (int.TryParse(inner, out var idx)) step = n => [Index(n, idx)];
                else throw Error($"unsupported index [{inner}]");
            }
            else if (f[i] == '"')
            {
                int close = i + 1;
                while (close < f.Length && f[close] != '"') close += f[close] == '\\' ? 2 : 1;
                var key = JsonNode.Parse(f[i..(close + 1)])!.GetValue<string>();
                i = close + 1;
                step = n => [Field(n, key)];
            }
            else
            {
                int s = i;
                while (i < f.Length && (char.IsLetterOrDigit(f[i]) || f[i] == '_')) i++;
                if (s == i) throw Error($"syntax error in '{f}'");
                var key = f[s..i];
                step = n => [Field(n, key)];
            }

            bool optional = i < f.Length && f[i] == '?';
            if (optional) i++;
            var st = step;
            cur = cur.SelectMany(n =>
            {
                if (!optional) return st(n);
                try { return st(n).ToList(); } catch (ShellException) { return []; }
            }).ToList();
        }
        return cur;
    }

    static JsonNode? Field(JsonNode? n, string key) => n switch
    {
        null => null,
        JsonObject o => o[key],
        _ => throw Error($"Cannot index {TypeOf(n)} with \"{key}\""),
    };

    static JsonNode? Index(JsonNode? n, int idx) => n switch
    {
        null => null,
        JsonArray a => (idx < 0 ? idx + a.Count : idx) is var k && k >= 0 && k < a.Count ? a[k] : null,
        _ => throw Error($"Cannot index {TypeOf(n)} with number"),
    };

    static IEnumerable<JsonNode?> Iterate(JsonNode? n) => n switch
    {
        JsonArray a => a.ToList(),
        JsonObject o => o.Select(p => p.Value).ToList(),
        _ => throw Error($"Cannot iterate over {TypeOf(n)}"),
    };
}
