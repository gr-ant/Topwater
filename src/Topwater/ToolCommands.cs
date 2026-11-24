using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Topwater;

/// <summary>A tool described by a name and a JSON Schema for its arguments (the shape MCP and function calling use).</summary>
public sealed record ToolSpec(string Name, string? Description, JsonElement InputSchema);

/// <summary>What a tool call returned. An error goes to stderr and makes the command exit 1.</summary>
public sealed record ToolResult(string Text, bool IsError = false);

/// <summary>Calls a tool by its original name with the arguments parsed from the command line.</summary>
public delegate Task<ToolResult> ToolInvoker(string tool, JsonObject arguments, CancellationToken cancellationToken);

public static class ToolCommandExtensions
{
    /// <summary>
    /// Exposes a set of schema-described tools as one program with a subcommand per tool:
    /// <c>program</c> lists them, <c>program TOOL --help</c> documents one, and
    /// <c>program TOOL --some-arg value</c> calls it. Options come from the tool's JSON Schema.
    /// </summary>
    public static Shell RegisterTools(this Shell shell, string program, IEnumerable<ToolSpec> tools, ToolInvoker invoke, string? description = null)
    {
        var list = tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
        var group = new ToolGroup(program, description, list, invoke);
        return shell.Register(program, group.RunAsync, description ?? $"{list.Count} tools; run `{program} --help`", $"{program} TOOL [--option value]...");
    }
}

sealed class ToolGroup(string program, string? description, List<ToolSpec> tools, ToolInvoker invoke)
{
    sealed record Param(string Name, string Flag, string Type, string? ItemType, bool Required, JsonElement Schema);

    static string Kebab(string s) => s.Replace('_', '-').Replace('.', '-').Replace('/', '-').Replace(' ', '-');
    static string FirstLine(string? s) => (s ?? "").Trim().Split('\n')[0].Trim();

    public async Task<int> RunAsync(CommandContext ctx)
    {
        var args = ctx.Args;
        if (args.Count == 0 || args[0] is "--help" or "-h" or "help")
        {
            ctx.Stdout.Write(GroupHelp());
            return 0;
        }
        var tool = tools.FirstOrDefault(t => t.Name == args[0]) ?? tools.FirstOrDefault(t => Kebab(t.Name) == Kebab(args[0]));
        if (tool is null)
        {
            ctx.Stderr.WriteLine($"{program}: unknown tool '{args[0]}'. Run `{program} --help` to list tools.");
            return 2;
        }
        var parameters = Params(tool);
        if (args.Skip(1).Any(a => a is "--help" or "-h"))
        {
            ctx.Stdout.Write(ToolHelp(tool, parameters));
            return 0;
        }

        JsonObject call;
        try { call = Parse(tool, parameters, args.Skip(1).ToList(), ctx); }
        catch (FormatException e)
        {
            ctx.Stderr.WriteLine($"{program} {Kebab(tool.Name)}: {e.Message}");
            ctx.Stderr.WriteLine($"Try `{program} {Kebab(tool.Name)} --help`.");
            return 2;
        }

        var result = await invoke(tool.Name, call, ctx.CancellationToken);
        var text = result.Text.Length == 0 || result.Text.EndsWith('\n') ? result.Text : result.Text + "\n";
        if (!result.IsError) { ctx.Stdout.Write(text); return 0; }
        ctx.Stderr.Write(text.Length == 0 ? $"{program} {Kebab(tool.Name)}: the tool reported an error\n" : text);
        return 1;
    }

    string GroupHelp()
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(description)) sb.Append(description.Trim()).Append("\n\n");
        sb.Append($"Usage: {program} TOOL [--option value]...\n       {program} TOOL --help\n\nTools:\n");
        int width = tools.Count == 0 ? 0 : Math.Min(32, tools.Max(t => Kebab(t.Name).Length));
        foreach (var t in tools)
            sb.Append("  ").Append(Kebab(t.Name).PadRight(width)).Append("  ").Append(FirstLine(t.Description)).Append('\n');
        return sb.ToString();
    }

    static List<Param> Params(ToolSpec tool)
    {
        var result = new List<Param>();
        if (tool.InputSchema.ValueKind != JsonValueKind.Object || !tool.InputSchema.TryGetProperty("properties", out var props) || props.ValueKind != JsonValueKind.Object)
            return result;
        var required = tool.InputSchema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array
            ? req.EnumerateArray().Select(r => r.GetString()).ToHashSet() : [];
        foreach (var p in props.EnumerateObject())
        {
            string type = TypeOf(p.Value);
            string? item = type == "array" && p.Value.TryGetProperty("items", out var items) ? TypeOf(items) : null;
            result.Add(new Param(p.Name, "--" + Kebab(p.Name), type, item, required.Contains(p.Name), p.Value));
        }
        return result;
    }

    /// <summary>The JSON Schema type of a property, looking through nullable unions; "" when it is not stated.</summary>
    static string TypeOf(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object) return "";
        if (schema.TryGetProperty("type", out var t))
        {
            if (t.ValueKind == JsonValueKind.String) return t.GetString()!;
            if (t.ValueKind == JsonValueKind.Array)
                return t.EnumerateArray().Select(x => x.GetString()).FirstOrDefault(x => x != "null") ?? "";
        }
        foreach (var key in new[] { "anyOf", "oneOf" })
            if (schema.TryGetProperty(key, out var options) && options.ValueKind == JsonValueKind.Array)
                foreach (var o in options.EnumerateArray())
                    if (TypeOf(o) is { Length: > 0 } and not "null" and var found) return found;
        return schema.TryGetProperty("enum", out _) ? "string" : "";
    }

    string ToolHelp(ToolSpec tool, List<Param> parameters)
    {
        var sb = new StringBuilder();
        var usage = string.Join(" ", parameters.Where(p => p.Required).Select(p => p.Type == "boolean" ? p.Flag : $"{p.Flag} {Placeholder(p)}"));
        sb.Append($"Usage: {program} {Kebab(tool.Name)}{(usage.Length > 0 ? " " + usage : "")}{(parameters.Any(p => !p.Required) ? " [options]" : "")}\n");
        if (!string.IsNullOrWhiteSpace(tool.Description)) sb.Append('\n').Append(tool.Description.Trim()).Append('\n');
        if (parameters.Count > 0)
        {
            sb.Append("\nOptions:\n");
            var left = parameters.Select(p => p.Type == "boolean" ? p.Flag : $"{p.Flag} {Placeholder(p)}").ToList();
            int width = Math.Min(34, left.Max(l => l.Length));
            for (int i = 0; i < parameters.Count; i++)
            {
                var p = parameters[i];
                var notes = new List<string>();
                if (p.Schema.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String) notes.Add(d.GetString()!.Trim().ReplaceLineEndings(" "));
                if (p.Required) notes.Add("(required)");
                if (p.Type == "array") notes.Add("(repeatable)");
                if (p.Schema.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array)
                    notes.Add("(one of: " + string.Join(", ", e.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText())) + ")");
                if (p.Schema.TryGetProperty("default", out var def)) notes.Add($"(default: {(def.ValueKind == JsonValueKind.String ? def.GetString() : def.GetRawText())})");
                sb.Append("  ").Append(left[i].PadRight(width)).Append("  ").Append(string.Join(" ", notes)).Append('\n');
            }
        }
        sb.Append("\n  --json JSON".PadRight(37)).Append("pass all arguments as one JSON object ('-' reads it from stdin)\n");
        return sb.ToString();
    }

    static string Placeholder(Param p) => (p.Type == "array" ? p.ItemType : p.Type) switch
    {
        "integer" => "INT", "number" => "NUM", "object" => "JSON", "string" => "TEXT", "boolean" => "BOOL", _ => "VALUE",
    };

    JsonObject Parse(ToolSpec tool, List<Param> parameters, List<string> args, CommandContext ctx)
    {
        var call = new JsonObject();
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (!arg.StartsWith("--") || arg.Length == 2)
                throw new FormatException($"unexpected argument '{arg}'; arguments are passed as --name value");
            string flag = arg, inline = "";
            bool hasInline = false;
            int eq = arg.IndexOf('=');
            if (eq > 0) { flag = arg[..eq]; inline = arg[(eq + 1)..]; hasInline = true; }

            string Next() => hasInline ? inline : ++i < args.Count ? args[i] : throw new FormatException($"option {flag} needs a value");

            if (flag == "--json")
            {
                string raw = Next();
                if (raw == "-") raw = ctx.StdinText;
                JsonNode? node;
                try { node = JsonNode.Parse(raw); } catch (JsonException e) { throw new FormatException($"--json is not valid JSON: {e.Message}"); }
                if (node is not JsonObject obj) throw new FormatException("--json must be a JSON object");
                foreach (var kv in obj.ToList()) { obj.Remove(kv.Key); call[kv.Key] = kv.Value; }
                continue;
            }

            var p = parameters.FirstOrDefault(x => x.Flag == flag || "--" + x.Name == flag);
            bool negated = false;
            if (p is null && flag.StartsWith("--no-") && parameters.FirstOrDefault(x => x.Type == "boolean" && x.Flag == "--" + flag[5..]) is { } neg)
            { p = neg; negated = true; }
            if (p is null)
            {
                var known = parameters.Count == 0 ? "this tool takes no options" : "options are " + string.Join(", ", parameters.Select(x => x.Flag));
                throw new FormatException($"unknown option {flag}; {known}");
            }

            if (p.Type == "boolean")
            {
                bool value = !negated;
                // a following true/false is taken as the value, so `--flag false` works as well as `--no-flag`
                if (hasInline) value = ParseBool(inline, flag);
                else if (!negated && i + 1 < args.Count && args[i + 1] is "true" or "false") value = args[++i] == "true";
                call[p.Name] = value;
            }
            else if (p.Type == "array")
            {
                string raw = Next();
                var array = call[p.Name] as JsonArray ?? [];
                if (raw.TrimStart().StartsWith('[') && TryJson(raw) is JsonArray whole)
                    foreach (var item in whole.ToList()) { whole.Remove(item); array.Add(item); }
                else array.Add(Convert(raw, p.ItemType ?? "", flag));
                call[p.Name] = array;
            }
            else call[p.Name] = Convert(Next(), p.Type, flag);
        }

        var missing = parameters.Where(p => p.Required && !call.ContainsKey(p.Name)).Select(p => p.Flag).ToList();
        if (missing.Count > 0) throw new FormatException("missing required option" + (missing.Count > 1 ? "s " : " ") + string.Join(", ", missing));
        return call;
    }

    static bool ParseBool(string s, string flag) => s.ToLowerInvariant() switch
    {
        "true" or "1" or "yes" => true,
        "false" or "0" or "no" => false,
        _ => throw new FormatException($"option {flag} expects true or false, got '{s}'"),
    };

    static JsonNode? TryJson(string raw)
    {
        try { return JsonNode.Parse(raw); } catch (JsonException) { return null; }
    }

    static JsonNode? Convert(string raw, string type, string flag)
    {
        switch (type)
        {
            case "string": return raw;
            case "integer":
                return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l) ? l
                    : throw new FormatException($"option {flag} expects a whole number, got '{raw}'");
            case "number":
                return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d
                    : throw new FormatException($"option {flag} expects a number, got '{raw}'");
            case "boolean": return ParseBool(raw, flag);
            case "object":
                return TryJson(raw) is JsonObject o ? o : throw new FormatException($"option {flag} expects a JSON object, got '{raw}'");
            default:
                // no declared type: JSON if it looks like JSON (numbers, true, objects, arrays), otherwise text
                return raw.Length > 0 && (char.IsDigit(raw[0]) || raw[0] is '{' or '[' or '-' or '"' || raw is "true" or "false" or "null") && TryJsonValue(raw, out var node) ? node : raw;
        }
    }

    static bool TryJsonValue(string raw, out JsonNode? node)
    {
        try { node = JsonNode.Parse(raw); return true; } catch (JsonException) { node = null; return false; }
    }
}
