using System.Text.RegularExpressions;

namespace Topwater;

/// <summary>Lookups into the generated tables (Generated/CommandSpec.g.cs, from spec/; see spec/README.md).</summary>
internal static partial class CommandSpec
{
    static readonly Regex Placeholder = new(@"\{([A-Za-z]\w*)(?::([\w-]+))?\}");

    /// <summary>The spec entry for a built-in command.</summary>
    internal static Entry Of(string name) => Commands[name];

    /// <summary>Fills a template: {x} as given, {x:shell} / {x:shell-always} shell-quoted, {x:locale} in ‘curly quotes’.</summary>
    internal static string Format(string template, params (string Name, string Value)[] values) =>
        Placeholder.Replace(template, m =>
        {
            var v = Array.Find(values, x => x.Name == m.Groups[1].Value).Value;
            if (v is null) return m.Value;
            return m.Groups[2].Value switch
            {
                "shell" => Builtins.Quote(v),
                "shell-always" => Builtins.QuoteAlways(v),
                "locale" => Builtins.QuoteL(v),
                _ => v,
            };
        });

    /// <summary>The command's message <paramref name="key"/> (or the shared default), filled in.</summary>
    internal static string Message(string command, string key, params (string Name, string Value)[] values) =>
        Format(Commands.TryGetValue(command, out var e) && e.Messages.TryGetValue(key, out var t) ? t : DefaultMessages[key], values);
}
