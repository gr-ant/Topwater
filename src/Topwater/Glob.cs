// Brace expansion and globbing. See docs/architecture.md.
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Topwater.Parsing;

namespace Topwater;

// ---------- brace expansion ----------

/// <summary>Brace expansion: a{b,c}d, {1..10}, {01..05}, {a..e}, {0..10..2}; a brace may span an expansion: {$x,y}.</summary>
internal static class Brace
{
    const int MaxItems = 10_000;
    const char Placeholder = '';

    public static IEnumerable<Word> Expand(Word w)
    {
        if (!w.Parts.Any(p => p is LiteralPart { Quoted: false } l && l.Text.Contains('{'))) return [w];
        // everything but unquoted text becomes one placeholder character, so braces work on plain text
        var text = new StringBuilder();
        var opaque = new List<WordPart>();
        foreach (var p in w.Parts)
            if (p is LiteralPart { Quoted: false } l) text.Append(l.Text);
            else { text.Append((char)(Placeholder + opaque.Count)); opaque.Add(p); }
        if (opaque.Count > 0x1000) return [w];
        var results = ExpandText(text.ToString());
        return results.Count == 1 && results[0] == text.ToString() ? [w] : results.Select(t => Rebuild(t, opaque));
    }

    static Word Rebuild(string text, List<WordPart> opaque)
    {
        var parts = new List<WordPart>();
        var lit = new StringBuilder();
        foreach (var c in text)
        {
            if (c < Placeholder || c >= Placeholder + opaque.Count) { lit.Append(c); continue; }
            if (lit.Length > 0) { parts.Add(new LiteralPart(lit.ToString(), false)); lit.Clear(); }
            parts.Add(opaque[c - Placeholder]);
        }
        if (lit.Length > 0 || parts.Count == 0) parts.Add(new LiteralPart(lit.ToString(), false));
        return new Word(parts);
    }

    static List<string> ExpandText(string text)
    {
        for (int open = text.IndexOf('{'); open >= 0; open = text.IndexOf('{', open + 1))
        {
            int close = FindClose(text, open);
            if (close < 0) continue;
            var alts = Alternatives(text[(open + 1)..close]);
            if (alts == null) continue;
            var results = new List<string>();
            foreach (var alt in alts)
            {
                results.AddRange(ExpandText(text[..open] + alt + text[(close + 1)..]));
                if (results.Count > MaxItems) throw new ShellException(CommandSpec.Diagnostics.BraceTooMany);
            }
            return results;
        }
        return [text];
    }

    static int FindClose(string s, int open)
    {
        int depth = 0;
        for (int i = open; i < s.Length; i++)
        {
            if (s[i] == '{') depth++;
            else if (s[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    static readonly Regex NumberRange = new(@"^(-?\d+)\.\.(-?\d+)(?:\.\.(-?\d+))?$");
    static readonly Regex LetterRange = new(@"^([a-zA-Z])\.\.([a-zA-Z])(?:\.\.(-?\d+))?$");

    static List<string>? Alternatives(string inner)
    {
        var m = NumberRange.Match(inner);
        if (m.Success)
        {
            long a = long.Parse(m.Groups[1].Value), b = long.Parse(m.Groups[2].Value);
            long step = m.Groups[3].Success ? Math.Abs(long.Parse(m.Groups[3].Value)) : 1;
            if (step == 0) step = 1;
            bool pad = m.Groups[1].Value.TrimStart('-').StartsWith('0') && m.Groups[1].Value.Length > 1
                       || m.Groups[2].Value.TrimStart('-').StartsWith('0') && m.Groups[2].Value.Length > 1;
            int width = Math.Max(m.Groups[1].Value.Length, m.Groups[2].Value.Length);
            if (Math.Abs(b - a) / step > MaxItems) throw new ShellException(CommandSpec.Diagnostics.BraceTooMany);
            var list = new List<string>();
            for (long v = a; a <= b ? v <= b : v >= b; v += a <= b ? step : -step)
                list.Add(pad ? v.ToString(CultureInfo.InvariantCulture).PadLeft(width, '0') : v.ToString(CultureInfo.InvariantCulture));
            return list;
        }
        m = LetterRange.Match(inner);
        if (m.Success)
        {
            char a = m.Groups[1].Value[0], b = m.Groups[2].Value[0];
            int step = m.Groups[3].Success ? Math.Max(1, Math.Abs(int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture))) : 1;
            var list = new List<string>();
            for (int c = a; a <= b ? c <= b : c >= b; c += a <= b ? step : -step) list.Add(((char)c).ToString());
            return list;
        }
        var parts = new List<string>();
        int depth = 0, start = 0;
        for (int i = 0; i < inner.Length; i++)
        {
            if (inner[i] == '{') depth++;
            else if (inner[i] == '}') depth--;
            else if (inner[i] == ',' && depth == 0) { parts.Add(inner[start..i]); start = i + 1; }
        }
        parts.Add(inner[start..]);
        return parts.Count < 2 ? null : parts;
    }
}

// ---------- globbing ----------

internal static class Glob
{
    public static bool HasWildcard(string s) => s.IndexOfAny(['*', '?', '[']) >= 0 || s.Contains("@(") || s.Contains("+(") || s.Contains("!(");

    public static string Escape(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s) { if (c is '*' or '?' or '[' or ']' or '\\' or '(' or ')' or '|' or '@' or '!' or '+') sb.Append('\\'); sb.Append(c); }
        return sb.ToString();
    }

    static bool HasUnescapedWildcard(string seg)
    {
        for (int i = 0; i < seg.Length; i++)
        {
            if (seg[i] == '\\') { i++; continue; }
            if (seg[i] is '*' or '?' or '[' || seg[i] is '@' or '+' or '!' && i + 1 < seg.Length && seg[i + 1] == '(') return true;
        }
        return false;
    }

    static string Unescape(string seg) => Regex.Replace(seg, @"\\(.)", "$1");

    static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, RegexOptions), Regex> Cache = new();

    /// <summary>A compiled-once regex: patterns repeat (a case in a loop), so building one per match would dominate.</summary>
    public static Regex Cached(string pattern, RegexOptions options, TimeSpan? timeout = null)
    {
        if (Cache.TryGetValue((pattern, options), out var re)) return re;
        if (Cache.Count >= 1024) Cache.Clear();
        return Cache[(pattern, options)] = new Regex(pattern, options, timeout ?? Shell.RegexTimeout);
    }

    /// <param name="slashes">True for case/[[ ]]/${x%pat} matching, where * also matches '/'.</param>
    public static Regex ToRegex(string pattern, bool ignoreCase = false, bool slashes = false) =>
        Cached("^" + ToRegexBody(pattern, slashes) + @"\z", (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None) | RegexOptions.Singleline);

    public static string ToRegexBody(string pattern, bool slashes = false)
    {
        var sb = new StringBuilder();
        int i = 0;
        Body(pattern, ref i, sb, slashes, inGroup: false);
        return sb.ToString();
    }

    /// <summary>Translates pattern from i, stopping at an unescaped | or ) when inside an extglob group.</summary>
    static void Body(string p, ref int i, StringBuilder sb, bool slashes, bool inGroup)
    {
        string any = slashes ? "." : "[^/]";
        for (; i < p.Length; i++)
        {
            char c = p[i];
            if (inGroup && c is '|' or ')') return;
            if (c is '@' or '!' or '*' or '+' or '?' && i + 1 < p.Length && p[i + 1] == '(' && GroupEnd(p, i + 1) >= 0)
            {
                // extglob: @(a|b) one of, *(..) any number, +(..) at least one, ?(..) optional, !(..) anything else
                i += 2;
                var alternatives = new List<string>();
                while (true)
                {
                    var alt = new StringBuilder();
                    Body(p, ref i, alt, slashes, inGroup: true);
                    alternatives.Add(alt.ToString());
                    if (i >= p.Length || p[i] == ')') break;
                    i++; // past '|'
                }
                var group = "(?:" + string.Join('|', alternatives) + ")";
                // shortcut: !(..) is exact only at the end of a pattern (rm !(*.txt)); make it a real complement if agents nest it
                sb.Append(c switch { '@' => group, '*' => group + "*", '+' => group + "+", '?' => group + "?", _ => $"(?:(?!{group}\\z){any}*)" });
                continue;
            }
            switch (c)
            {
                case '\\' when i + 1 < p.Length: sb.Append(Regex.Escape(p[++i].ToString())); break;
                case '*': sb.Append(any).Append('*'); break;
                case '?': sb.Append(any); break;
                case '[' when BracketEnd(p, i) is var close and > 0:
                    sb.Append(Bracket(p[(i + 1)..close]));
                    i = close;
                    break;
                default: sb.Append(Regex.Escape(c.ToString())); break;
            }
        }
    }

    /// <summary>The ')' closing the extglob group that opens at i, or -1.</summary>
    static int GroupEnd(string p, int i)
    {
        int depth = 0;
        for (; i < p.Length; i++)
        {
            if (p[i] == '\\') { i++; continue; }
            if (p[i] == '(') depth++;
            else if (p[i] == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>The ']' closing the bracket expression that opens at i (allowing []...] and [[:class:]]), or -1.</summary>
    static int BracketEnd(string p, int i)
    {
        int k = i + 1;
        if (k < p.Length && p[k] is '!' or '^') k++;
        if (k < p.Length && p[k] == ']') k++;
        for (; k < p.Length; k++)
        {
            if (p[k] == '[' && k + 1 < p.Length && p[k + 1] is ':' or '.' or '=')
            {
                int e = p.IndexOf(p[k + 1] + "]", k + 2, StringComparison.Ordinal);
                if (e < 0) return -1;
                k = e + 1;
                continue;
            }
            if (p[k] == '\\') { k++; continue; }
            if (p[k] == ']') return k;
        }
        return -1;
    }

    static string Bracket(string inner)
    {
        var sb = new StringBuilder("[");
        int k = 0;
        if (inner.Length > 0 && inner[0] is '!' or '^') { sb.Append('^'); k = 1; }
        for (; k < inner.Length; k++)
        {
            char c = inner[k];
            if (c == '[' && k + 1 < inner.Length && inner[k + 1] == ':' && inner.IndexOf(":]", k + 2, StringComparison.Ordinal) is var e and >= 0)
            {
                sb.Append(Builtins.PosixClasses.GetValueOrDefault(inner[(k + 2)..e], ""));
                k = e + 1;
                continue;
            }
            if (c == '\\' && k + 1 < inner.Length) c = inner[++k];
            if (c is '\\' or '[' or ']' or '^') sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append(']').ToString();
    }

    /// <summary>Matches a glob pattern against the virtual filesystem; returns paths in the same form (relative/absolute) as the pattern.</summary>
    public static List<string> Expand(Shell shell, string pattern)
    {
        bool absolute = pattern.StartsWith('/');
        var current = new List<(string Abs, string Display)> { (absolute ? "/" : shell.Cwd, absolute ? "/" : "") };
        bool dotglob = shell.Shopts.Contains("dotglob"), globstar = shell.Shopts.Contains("globstar");
        var segs = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int k = 0; k < segs.Length; k++)
        {
            var seg = segs[k];
            var next = new List<(string, string)>();
            if (seg == "**" && globstar) // zero or more directories; as the last segment, every file and directory below
            {
                bool last = !pattern.EndsWith('/') && k == segs.Length - 1;
                var queue = new Queue<(string Abs, string Display)>(current);
                while (queue.Count > 0)
                {
                    var (abs, display) = queue.Dequeue();
                    bool isBase = current.Contains((abs, display));
                    if (!last) next.Add((abs, display));
                    else if (display.Length > 0 && isBase) next.Add((abs, Join(display, ""))); // bash lists the base itself as "dir/"
                    if (shell.Fs.IsDirectory(abs) && shell.Fs.CanRead(abs))
                    {
                        foreach (var name in shell.Fs.List(abs))
                        {
                            var child = VirtualFileSystem.Normalize(abs, name);
                            if (!dotglob && name.StartsWith('.')) continue;
                            if (shell.Fs.IsDirectory(child, follow: false)) queue.Enqueue((child, Join(display, name))); // like bash, ** does not follow links
                            else if (last) next.Add((child, Join(display, name)));
                        }
                        if (last && !isBase) next.Add((abs, display));
                    }
                }
                current = next;
                continue;
            }
            foreach (var (abs, display) in current)
            {
                if (!shell.Fs.IsDirectory(abs) || !shell.Fs.CanRead(abs)) continue;
                if (seg is "." or "..") { next.Add((VirtualFileSystem.Normalize(abs, seg), Join(display, seg))); continue; }
                if (HasUnescapedWildcard(seg))
                {
                    var re = ToRegex(seg, shell.Shopts.Contains("nocaseglob"));
                    foreach (var name in shell.Fs.List(abs))
                        if (re.IsMatch(name) && (!name.StartsWith('.') || seg.StartsWith('.') || dotglob))
                            next.Add((VirtualFileSystem.Normalize(abs, name), Join(display, name)));
                }
                else
                {
                    var name = Unescape(seg);
                    var child = VirtualFileSystem.Normalize(abs, name);
                    if (shell.Fs.Exists(child, follow: false)) next.Add((child, Join(display, name)));
                }
            }
            current = next;
        }
        if (pattern.EndsWith('/')) // `echo */` lists directories only
            return current.Where(c => c.Display.Length > 0 && shell.Fs.IsDirectory(c.Abs))
                .Select(c => c.Display == "/" ? "/" : c.Display + "/").Order(StringComparer.Ordinal).ToList();
        return current.Select(c => c.Display).Where(d => d.Length > 0).Order(StringComparer.Ordinal).ToList();
    }

    static string Join(string display, string name) => display switch { "" => name, "/" => "/" + name, _ => display + "/" + name };
}
