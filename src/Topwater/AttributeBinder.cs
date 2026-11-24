using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Topwater;

/// <summary>
/// Exposes a method as a shell command. Parameters become <c>--kebab-case</c> options (and positionals, in order);
/// bool parameters become flags. Use <see cref="DescriptionAttribute"/> on parameters for --help text.
/// Return string (stdout), int (exit code), Task of either, or any object (printed as JSON).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CommandAttribute(string name) : Attribute
{
    public string Name { get; } = name;
    public string Description { get; set; } = "";
}

internal static class AttributeBinder
{
    public static void Register(Shell shell, object target)
    {
        var type = target as Type ?? target.GetType();
        var instance = target is Type ? null : target;
        var flags = BindingFlags.Public | BindingFlags.Static | (instance != null ? BindingFlags.Instance : 0);
        foreach (var method in type.GetMethods(flags))
        {
            if (method.GetCustomAttribute<CommandAttribute>() is not { } attr) continue;
            var bound = new BoundCommand(method, method.IsStatic ? null : instance, attr);
            shell.Register(attr.Name, bound.InvokeAsync, attr.Description, bound.Usage);
        }
    }

    static string Kebab(string name) => Regex.Replace(name, "([a-z0-9])([A-Z])", "$1-$2").ToLowerInvariant();

    sealed class BoundCommand
    {
        readonly MethodInfo _method;
        readonly object? _target;
        readonly ParameterInfo[] _params;
        readonly string[] _optionNames;

        public BoundCommand(MethodInfo method, object? target, CommandAttribute attr)
        {
            _method = method;
            _target = target;
            _params = method.GetParameters();
            _optionNames = _params.Select(p => Kebab(p.Name!)).ToArray();
            Usage = BuildUsage(attr);
        }

        public string Usage { get; }

        static bool IsInjected(ParameterInfo p) =>
            p.ParameterType == typeof(CommandContext) || p.ParameterType == typeof(CancellationToken) || p.ParameterType == typeof(TextReader);

        static bool IsFlag(ParameterInfo p) => (Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType) == typeof(bool);

        static bool IsRequired(ParameterInfo p) => !p.HasDefaultValue && !IsFlag(p) && !p.ParameterType.IsArray;

        string BuildUsage(CommandAttribute attr)
        {
            var sb = new StringBuilder($"Usage: {attr.Name}");
            var options = new List<(string, string)>();
            for (int i = 0; i < _params.Length; i++)
            {
                var p = _params[i];
                if (IsInjected(p)) continue;
                var type = p.ParameterType.IsArray ? p.ParameterType.GetElementType()! : Nullable.GetUnderlyingType(p.ParameterType) ?? p.ParameterType;
                var left = IsFlag(p) ? $"--{_optionNames[i]}" : $"--{_optionNames[i]} <{TypeName(type)}>";
                sb.Append(IsRequired(p) ? $" {left}" : $" [{left}]");
                var desc = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
                if (type.IsEnum) desc += $" ({string.Join("|", Enum.GetNames(type).Select(n => n.ToLowerInvariant()))})";
                if (p.HasDefaultValue && p.DefaultValue != null && !IsFlag(p)) desc += $" [default: {p.DefaultValue}]";
                if (IsRequired(p)) desc += " (required)";
                options.Add((left, desc.Trim()));
            }
            if (attr.Description.Length > 0) sb.Append("\n\n").Append(attr.Description);
            if (options.Count > 0)
            {
                sb.Append("\n\nOptions:");
                int width = options.Max(o => o.Item1.Length) + 2;
                foreach (var (left, desc) in options) sb.Append("\n  ").Append(left.PadRight(width)).Append(desc);
            }
            return sb.ToString();
        }

        static string TypeName(Type t) =>
            t == typeof(string) ? "text" : t == typeof(int) || t == typeof(long) ? "int" : t == typeof(double) || t == typeof(decimal) || t == typeof(float) ? "number" : t.IsEnum ? "choice" : t.Name.ToLowerInvariant();

        ShellException Error(string message) => new($"{message}\n{Usage}", 2);

        public async Task<int> InvokeAsync(CommandContext ctx)
        {
            if (ctx.Args.Contains("--help") || ctx.Args.Contains("-h"))
            {
                ctx.Stdout.WriteLine(Usage);
                return 0;
            }

            var values = new object?[_params.Length];
            var set = new bool[_params.Length];
            var lists = new Dictionary<int, List<string>>();
            var positional = new List<string>();

            void Assign(int i, string raw)
            {
                var p = _params[i];
                if (p.ParameterType.IsArray) { (lists.TryGetValue(i, out var l) ? l : lists[i] = []).Add(raw); }
                else values[i] = Convert(raw, p.ParameterType, _optionNames[i]);
                set[i] = true;
            }

            var args = ctx.Args;
            for (int a = 0; a < args.Count; a++)
            {
                var arg = args[a];
                if (arg == "--") { positional.AddRange(args.Skip(a + 1)); break; }
                if (!arg.StartsWith("--") || arg.Length == 2) { positional.Add(arg); continue; }

                var name = arg[2..];
                string? inline = null;
                if (name.IndexOf('=') is var eq and > 0) { inline = name[(eq + 1)..]; name = name[..eq]; }
                int idx = Array.FindIndex(_optionNames, n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
                if (idx < 0 || IsInjected(_params[idx])) throw Error($"unknown option '--{name}'");
                if (IsFlag(_params[idx])) Assign(idx, inline ?? "true");
                else if (inline != null) Assign(idx, inline);
                else if (a + 1 < args.Count) Assign(idx, args[++a]);
                else throw Error($"option '--{name}' requires a value");
            }

            int next = 0;
            for (int i = 0; i < _params.Length && next < positional.Count; i++)
            {
                var p = _params[i];
                if (IsInjected(p) || IsFlag(p) || (set[i] && !p.ParameterType.IsArray)) continue;
                if (p.ParameterType.IsArray) { while (next < positional.Count) Assign(i, positional[next++]); }
                else Assign(i, positional[next++]);
            }
            if (next < positional.Count) throw Error($"unexpected argument '{positional[next]}'");

            for (int i = 0; i < _params.Length; i++)
            {
                var p = _params[i];
                if (p.ParameterType == typeof(CommandContext)) values[i] = ctx;
                else if (p.ParameterType == typeof(CancellationToken)) values[i] = ctx.CancellationToken;
                else if (p.ParameterType == typeof(TextReader)) values[i] = ctx.Stdin;
                else if (p.ParameterType.IsArray)
                {
                    var items = lists.GetValueOrDefault(i) ?? [];
                    var elem = p.ParameterType.GetElementType()!;
                    var arr = Array.CreateInstance(elem, items.Count);
                    for (int k = 0; k < items.Count; k++) arr.SetValue(Convert(items[k], elem, _optionNames[i]), k);
                    values[i] = arr;
                }
                else if (!set[i])
                {
                    if (IsFlag(p) && !p.HasDefaultValue) values[i] = false;
                    else if (p.HasDefaultValue) values[i] = p.DefaultValue;
                    else throw Error($"missing required option --{_optionNames[i]}");
                }
            }

            object? result;
            try { result = _method.Invoke(_target, values); }
            catch (TargetInvocationException e) when (e.InnerException != null) { ExceptionDispatchInfo.Throw(e.InnerException); throw; }

            if (result is Task task)
            {
                await task;
                var rt = _method.ReturnType;
                result = rt.IsGenericType && rt.GetGenericTypeDefinition() == typeof(Task<>) ? rt.GetProperty("Result")!.GetValue(task) : null;
            }

            switch (result)
            {
                case null: return 0;
                case int code: return code;
                case string s:
                    ctx.Stdout.Write(s);
                    if (s.Length > 0 && !s.EndsWith('\n')) ctx.Stdout.WriteLine();
                    return 0;
                default:
                    ctx.Stdout.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
                    return 0;
            }
        }

        object? Convert(string raw, Type type, string option)
        {
            var t = Nullable.GetUnderlyingType(type) ?? type;
            try
            {
                if (t == typeof(string)) return raw;
                if (t.IsEnum) return Enum.Parse(t, raw.Replace("-", ""), ignoreCase: true);
                if (t == typeof(bool))
                    return raw.ToLowerInvariant() switch { "true" or "yes" or "1" => true, "false" or "no" or "0" => false, _ => throw new FormatException() };
                return System.Convert.ChangeType(raw, t, CultureInfo.InvariantCulture);
            }
            catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException or ArgumentException)
            {
                throw Error($"invalid value '{raw}' for --{option}: expected {TypeName(t)}");
            }
        }
    }
}
