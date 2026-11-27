using System.Security.Cryptography;
using System.Text;

namespace Topwater;

/// <summary>Binary inspection: xxd, hexdump, od, strings. All of them read true bytes (text files: their UTF-8).</summary>
internal static partial class Builtins
{
    static int Num(string s)
    {
        try { return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(s[2..], 16) : int.Parse(s); }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException) { throw new ShellException(CommandSpec.Message("xxd", "invalidNumber", ("value", s))); }
    }

    /// <summary>Python-style slice: a negative start counts from the end; out-of-range bounds are clamped.</summary>
    static byte[] Slice(byte[] data, int start, int? length = null)
    {
        start = start < 0 ? Math.Max(0, data.Length + start) : Math.Min(start, data.Length);
        int end = length is { } n ? Math.Clamp(start + n, start, data.Length) : data.Length;
        return data[start..end];
    }

    static byte[] ReadAll(CommandContext ctx, IReadOnlyList<string> files, Action onError) => InputBytes(ctx, files, onError).SelectMany(i => i.Text).ToArray();

    static string Printable(byte[] chunk) => new(chunk.Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray());

    /// <summary>(offset, chunk) per line, with a null chunk for a "*" line where full lines repeat (hexdump/od style).</summary>
    static IEnumerable<(int Offset, byte[]? Chunk)> Lines(byte[] data, int width, bool squeeze)
    {
        byte[]? prev = null;
        bool starred = false;
        for (int i = 0; i < data.Length; i += width)
        {
            var chunk = data[i..Math.Min(i + width, data.Length)];
            if (squeeze && prev != null && chunk.Length == width && chunk.AsSpan().SequenceEqual(prev))
            {
                if (!starred) { starred = true; yield return (i, null); }
                continue;
            }
            prev = chunk;
            starred = false;
            yield return (i, chunk);
        }
    }

    static string Octal(byte b) => Convert.ToString(b, 8);

    /// <summary>A byte as cmp -b shows it: printable as itself, control bytes as ^X, high bytes as M- and the low byte's form.</summary>
    static string Visible(byte b) => b >= 128 ? "M-" + Visible((byte)(b - 128)) : b < 32 ? "^" + (char)(b + 64) : b == 127 ? "^?" : ((char)b).ToString();

    static readonly Dictionary<byte, string> OdEscapes = new() { [0] = "\\0", [7] = "\\a", [8] = "\\b", [9] = "\\t", [10] = "\\n", [11] = "\\v", [12] = "\\f", [13] = "\\r" };

    static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    static void RegisterBinaryCommands(Shell shell)
    {
        shell.Register("cmp", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("cmp"));
            var ops = a.Positional;
            if (ops.Count == 0) throw new UsageException(CommandSpec.Message("cmp", "missingOperand"));
            if (ops.Count > 4) throw new UsageException(CommandSpec.Message("cmp", "extraOperand", ("operand", ops[4])));
            long Number(string? s, string option) => s == null ? 0
                : long.TryParse(s, out var n) && n >= 0 ? n : throw new UsageException(CommandSpec.Message("cmp", "invalidNumber", ("option", option), ("value", s)));
            var skips = (a.Value('i') ?? "").Split(':');
            long skip1 = ops.Count > 2 ? Number(ops[2], "ignore-initial") : Number(a.Value('i') == null ? null : skips[0], "ignore-initial");
            long skip2 = ops.Count > 3 ? Number(ops[3], "ignore-initial") : Number(a.Value('i') == null ? null : skips[^1], "ignore-initial");
            long? limit = a.Value('n') is { } n ? Number(n, "bytes") : null;
            bool silent = a.Has('s'), verbose = a.Has('l'), bytes = a.Has('b');
            var names = new[] { ops[0], ops.Count > 1 ? ops[1] : "-" };
            var data = new List<byte[]>();
            foreach (var (name, text) in Inputs(ctx, names, () => { }, ctx.Fs.ReadAllBytes, () => ByteText.Encode(ctx.StdinText), silent)) data.Add(text);
            if (data.Count < 2) return 2;
            var x = Slice(data[0], (int)Math.Min(skip1, data[0].Length));
            var y = Slice(data[1], (int)Math.Min(skip2, data[1].Length));
            long common = Math.Min(x.Length, y.Length), end = limit is { } l ? Math.Min(common, l) : common;
            // shortcut: -l pads positions to the digits of the compared length, which matches GNU for ordinary files
            int width = Math.Max(1, (limit is { } m ? Math.Min(Math.Max(x.Length, y.Length), m) : Math.Max(x.Length, y.Length)).ToString().Length);
            long line = 1;
            bool differ = false;
            for (long i = 0; i < end; i++)
            {
                if (x[i] == y[i]) { if (x[i] == '\n') line++; continue; }
                differ = true;
                if (silent) return 1;
                if (!verbose)
                {
                    ctx.Stdout.WriteLine(bytes
                        ? CommandSpec.Message("cmp", "differBytes", ("file1", names[0]), ("file2", names[1]), ("byte", (i + 1).ToString()), ("line", line.ToString()), ("byte1", $"{Octal(x[i]),3} {Visible(x[i])}"), ("byte2", $"{Octal(y[i]),3} {Visible(y[i])}"))
                        : CommandSpec.Message("cmp", "differ", ("file1", names[0]), ("file2", names[1]), ("byte", (i + 1).ToString()), ("line", line.ToString())));
                    return 1;
                }
                var pos = (i + 1).ToString().PadLeft(width);
                ctx.Stdout.WriteLine(bytes ? $"{pos} {Octal(x[i]),3} {Visible(x[i]),-4} {Octal(y[i]),3} {Visible(y[i])}" : $"{pos} {Octal(x[i]),3} {Octal(y[i]),3}");
            }
            if (x.Length == y.Length || (limit is { } lim && end >= lim)) return differ ? 1 : 0;
            if (!silent)
            {
                var (shortName, shorter) = x.Length < y.Length ? (names[0], x) : (names[1], y);
                var newlines = shorter.Count(b => b == '\n');
                ctx.Stderr.WriteLine("cmp: " + (shorter.Length == 0 ? CommandSpec.Message("cmp", "eofEmpty", ("file", shortName))
                    : verbose ? CommandSpec.Message("cmp", "eofVerbose", ("file", shortName), ("byte", shorter.Length.ToString()))
                    : shorter[^1] == '\n' ? CommandSpec.Message("cmp", "eof", ("file", shortName), ("byte", shorter.Length.ToString()), ("line", newlines.ToString()))
                    : CommandSpec.Message("cmp", "eofInLine", ("file", shortName), ("byte", shorter.Length.ToString()), ("line", (newlines + 1).ToString()))));
            }
            return 1;
        });

        shell.Register("xxd", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("xxd"));
            int code = 0;
            var data = ReadAll(ctx, a.Positional.Take(1).ToList(), () => code = 1); // a second operand is xxd's outfile; not supported
            int start = Num(a.Value('s') ?? "0");
            data = Slice(data, start, a.Value('l') is { } l ? Num(l) : null);
            start = Math.Max(0, start < 0 ? start + data.Length : start);
            int cols = Num(a.Value('c') ?? (a.Has('p') ? "30" : "16"));
            if (cols <= 0) throw new ShellException(CommandSpec.Message("xxd", "invalidColumns"));
            for (int i = 0; i < data.Length; i += cols)
            {
                var chunk = data[i..Math.Min(i + cols, data.Length)];
                if (a.Has('p')) { ctx.Stdout.WriteLine(Convert.ToHexString(chunk).ToLowerInvariant()); continue; }
                var hex = string.Concat(chunk.Select((b, k) => b.ToString("x2") + (k % 2 == 1 ? " " : "")));
                ctx.Stdout.WriteLine($"{start + i:x8}: {hex.PadRight(cols * 2 + cols / 2)} {Printable(chunk)}");
            }
            return code;
        });

        shell.Register("hexdump", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("hexdump"));
            int code = 0;
            var data = Slice(ReadAll(ctx, a.Positional, () => code = 1), Num(a.Value('s') ?? "0"), a.Value('n') is { } n ? Num(n) : null);
            foreach (var (i, chunk) in Lines(data, 16, !a.Has('v')))
            {
                if (chunk == null) { ctx.Stdout.WriteLine("*"); continue; }
                var hex = string.Concat(chunk.Select((b, k) => b.ToString("x2") + " " + (k == 7 ? " " : "")));
                ctx.Stdout.WriteLine($"{i:x8}  {hex,-49} |{Printable(chunk)}|");
            }
            if (data.Length > 0) ctx.Stdout.WriteLine($"{data.Length:x8}");
            return code;
        });

        shell.Register("od", ctx =>
        {
            var a = Args.Parse(ctx.Args, CommandSpec.Of("od"));
            int code = 0, failed = 0;
            var data = Slice(ReadAll(ctx, a.Positional, () => { code = 1; failed++; }), Num(a.Value('j') ?? "0"), a.Value('N') is { } n ? Num(n) : null);
            if (failed > 0 && failed == a.Positional.Count) return 1; // no input could be read: GNU od prints no offset either
            var fmt = a.Value('t') ?? (a.Has('c') ? "c" : a.Has('x') ? "x2" : "o2");
            fmt = fmt switch { "x" => "x4", "o" => "o4", _ => fmt };
            Func<byte[], string> cells;
            if (fmt == "c")
                cells = chunk => string.Concat(chunk.Select(b => (OdEscapes.GetValueOrDefault(b) ?? (b is >= 32 and < 127 ? ((char)b).ToString() : Convert.ToString(b, 8).PadLeft(3, '0'))).PadLeft(4)));
            else if (fmt.Length == 2 && fmt[0] is 'x' or 'o' && fmt[1] is '1' or '2' or '4' or '8')
            {
                int size = fmt[1] - '0', digits = fmt[0] == 'x' ? size * 2 : (size * 8 + 2) / 3;
                cells = chunk =>
                {
                    var padded = chunk.Concat(new byte[(size - chunk.Length % size) % size]).ToArray(); // od pads a partial last word with zero bytes
                    var sb = new StringBuilder();
                    for (int k = 0; k < padded.Length; k += size)
                    {
                        ulong w = 0;
                        for (int j = size - 1; j >= 0; j--) w = (w << 8) | padded[k + j];
                        sb.Append(' ').Append((fmt[0] == 'x' ? w.ToString("x") : Convert.ToString((long)w, 8)).PadLeft(digits, '0'));
                    }
                    return sb.ToString();
                };
            }
            else throw new ShellException(CommandSpec.Message("od", "invalidType", ("type", fmt)));
            var radix = a.Value('A') ?? "o";
            if (radix is not ("o" or "x" or "d" or "n")) throw new ShellException(CommandSpec.Message("od", "invalidRadix", ("radix", radix)));
            string Addr(int v) => radix switch { "n" => "", "o" => Convert.ToString(v, 8).PadLeft(7, '0'), "x" => v.ToString("x6"), _ => v.ToString("D7") };
            foreach (var (i, chunk) in Lines(data, 16, !a.Has('v'))) ctx.Stdout.WriteLine(chunk == null ? "*" : Addr(i) + cells(chunk));
            if (radix != "n") ctx.Stdout.WriteLine(Addr(data.Length));
            return code;
        });

        shell.Register("strings", ctx =>
        {
            var a = Args.Parse(Args.NumericShorthand(ctx.Args), CommandSpec.Of("strings"));
            int code = 0, minimum = Num(a.Value('n') ?? "4");
            foreach (var (_, data) in InputBytes(ctx, a.Positional, () => code = 1))
            {
                var run = new StringBuilder();
                foreach (var b in data.Append((byte)0))
                {
                    if (b is >= 32 and < 127 or 9) { run.Append((char)b); continue; }
                    if (run.Length >= minimum) ctx.Stdout.WriteLine(run.ToString());
                    run.Clear();
                }
            }
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
    }
}
