using System.Text;

namespace Topwater;

/// <summary>Binary inspection: xxd, hexdump, od, strings. All of them read true bytes (text files: their UTF-8).</summary>
internal static partial class Builtins
{
    static int Num(string s)
    {
        try { return s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(s[2..], 16) : int.Parse(s); }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException) { throw new ShellException($"invalid number '{s}'"); }
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

    static readonly Dictionary<byte, string> OdEscapes = new() { [0] = "\\0", [7] = "\\a", [8] = "\\b", [9] = "\\t", [10] = "\\n", [11] = "\\v", [12] = "\\f", [13] = "\\r" };

    static void RegisterBinaryCommands(Shell shell)
    {
        shell.Register("xxd", ctx =>
        {
            var a = Args.Parse(ctx.Args, "p", "lsc", "plain=p,len=l,seek=s,cols=c");
            int code = 0;
            var data = ReadAll(ctx, a.Positional.Take(1).ToList(), () => code = 1); // a second operand is xxd's outfile; not supported
            int start = Num(a.Value('s') ?? "0");
            data = Slice(data, start, a.Value('l') is { } l ? Num(l) : null);
            start = Math.Max(0, start < 0 ? start + data.Length : start);
            int cols = Num(a.Value('c') ?? (a.Has('p') ? "30" : "16"));
            if (cols <= 0) throw new ShellException("invalid number of columns");
            for (int i = 0; i < data.Length; i += cols)
            {
                var chunk = data[i..Math.Min(i + cols, data.Length)];
                if (a.Has('p')) { ctx.Stdout.WriteLine(Convert.ToHexString(chunk).ToLowerInvariant()); continue; }
                var hex = string.Concat(chunk.Select((b, k) => b.ToString("x2") + (k % 2 == 1 ? " " : "")));
                ctx.Stdout.WriteLine($"{start + i:x8}: {hex.PadRight(cols * 2 + cols / 2)} {Printable(chunk)}");
            }
            return code;
        }, "Make a hex dump (-p plain, -l LEN, -s OFFSET)");

        shell.Register("hexdump", ctx =>
        {
            var a = Args.Parse(ctx.Args, "Cv", "ns", "canonical=C,length=n,skip=s,no-squeezing=v");
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
        }, "Display file contents in hexadecimal (-C canonical, -n LEN)");

        shell.Register("od", ctx =>
        {
            var a = Args.Parse(ctx.Args, "cxov", "ANtj", "address-radix=A,read-bytes=N,format=t,skip-bytes=j");
            int code = 0;
            var data = Slice(ReadAll(ctx, a.Positional, () => code = 1), Num(a.Value('j') ?? "0"), a.Value('N') is { } n ? Num(n) : null);
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
            else throw new ShellException($"invalid type string '{fmt}' (supported here: c, x1, x2, x4, x8, o1, o2, o4, o8)");
            var radix = a.Value('A') ?? "o";
            if (radix is not ("o" or "x" or "d" or "n")) throw new ShellException($"invalid output address radix '{radix}'; it must be one character from [doxn]");
            string Addr(int v) => radix switch { "n" => "", "o" => Convert.ToString(v, 8).PadLeft(7, '0'), "x" => v.ToString("x6"), _ => v.ToString("D7") };
            foreach (var (i, chunk) in Lines(data, 16, !a.Has('v'))) ctx.Stdout.WriteLine(chunk == null ? "*" : Addr(i) + cells(chunk));
            if (radix != "n") ctx.Stdout.WriteLine(Addr(data.Length));
            return code;
        }, "Dump files in octal and other formats (-c, -x, -t x1, -An, -N LEN)");

        shell.Register("strings", ctx =>
        {
            var a = Args.Parse(Args.NumericShorthand(ctx.Args), "a", "n", "bytes=n,all=a");
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
        }, "Print printable character sequences in files (-n MIN)");
    }
}
