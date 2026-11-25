using System.Text;

namespace Topwater;

/// <summary>
/// Carries arbitrary bytes through the shell's string pipes and redirects without loss (Python's "surrogateescape"):
/// valid UTF-8 decodes as usual, and each byte that isn't valid UTF-8 becomes the lone surrogate U+DC80..U+DCFF.
/// Text is unaffected; binary output (gzip -c, tar cf -) survives `|` and `>`, and files store the original bytes.
/// </summary>
internal static class ByteText
{
    const char First = '\uDC80', Last = '\uDCFF';

    public static string Decode(byte[] data)
    {
        if (System.Text.Unicode.Utf8.IsValid(data)) return Encoding.UTF8.GetString(data);
        var sb = new StringBuilder(data.Length);
        var rest = data.AsSpan();
        while (rest.Length > 0)
        {
            if (Rune.DecodeFromUtf8(rest, out var rune, out int used) == System.Buffers.OperationStatus.Done) sb.Append(rune.ToString());
            else { sb.Append((char)(First + rest[0] - 0x80)); used = 1; } // only bytes >= 0x80 can be invalid
            rest = rest[used..];
        }
        return sb.ToString();
    }

    public static bool HasEscapes(string? text) => text != null && text.AsSpan().IndexOfAnyInRange(First, Last) >= 0;

    /// <summary>The bytes <paramref name="text"/> stands for: UTF-8, with escaped bytes restored.</summary>
    public static byte[] Encode(string text)
    {
        if (!HasEscapes(text)) return Encoding.UTF8.GetBytes(text);
        var bytes = new List<byte>(text.Length);
        Span<byte> buf = stackalloc byte[4];
        var rest = text.AsSpan();
        while (rest.Length > 0)
        {
            if (Rune.DecodeFromUtf16(rest, out var rune, out int used) == System.Buffers.OperationStatus.Done)
                foreach (var b in buf[..rune.EncodeToUtf8(buf)]) bytes.Add(b);
            else if (rest[0] is >= First and <= Last) bytes.Add((byte)(rest[0] - First + 0x80));
            else bytes.AddRange([0xEF, 0xBF, 0xBD]); // any other lone surrogate: U+FFFD, as UTF-8 encoding would
            rest = rest[used..];
        }
        return [.. bytes];
    }

    /// <summary>For text leaving the shell: escaped bytes show as U+FFFD, as a lossy UTF-8 decode would.</summary>
    public static string Printable(string text) => HasEscapes(text) ? Encoding.UTF8.GetString(Encode(text)) : text;
}
