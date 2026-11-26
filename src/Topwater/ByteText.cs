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

    /// <summary>Escaped bytes that together spell valid UTF-8 become those characters (<c>printf '\xc3\xa9'</c> is é); the rest stay escaped.</summary>
    public static string Rejoin(string text) => HasEscapes(text) ? Decode(Encode(text)) : text;

    /// <summary>For text leaving the shell: escaped bytes show as U+FFFD, as a lossy UTF-8 decode would.</summary>
    public static string Printable(string text) => HasEscapes(text) ? Encoding.UTF8.GetString(Encode(text)) : text;
}

/// <summary>
/// Strings in .topwater JSON as Python and TypeScript write them: an escaped byte (a lone surrogate) is the JSON escape
/// <c>\udcXX</c>, which System.Text.Json would otherwise write as U+FFFD and refuse to read.
/// shortcut: object keys still go through the default writer (U+FFFD); only array keys could hold such a byte.
/// </summary>
internal sealed class EscapedStringConverter : System.Text.Json.Serialization.JsonConverter<string>
{
    public override string Read(ref System.Text.Json.Utf8JsonReader r, Type type, System.Text.Json.JsonSerializerOptions o) => ReadString(ref r);
    public override string ReadAsPropertyName(ref System.Text.Json.Utf8JsonReader r, Type type, System.Text.Json.JsonSerializerOptions o) => ReadString(ref r);
    public override void WriteAsPropertyName(System.Text.Json.Utf8JsonWriter w, string value, System.Text.Json.JsonSerializerOptions o) => w.WritePropertyName(value);

    public override void Write(System.Text.Json.Utf8JsonWriter w, string value, System.Text.Json.JsonSerializerOptions o)
    {
        if (value.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF') < 0) { w.WriteStringValue(value); return; }
        var sb = new StringBuilder(value.Length + 8).Append('"');
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) sb.Append(c).Append(value[++i]);
            else if (char.IsSurrogate(c) || c < 0x20 || c == 0x7f) sb.Append($"\\u{(int)c:x4}");
            else if (c is '"' or '\\') sb.Append('\\').Append(c);
            else sb.Append(c);
        }
        w.WriteRawValue(sb.Append('"').ToString(), skipInputValidation: true);
    }

    static string ReadString(ref System.Text.Json.Utf8JsonReader r)
    {
        if (r.TokenType is not (System.Text.Json.JsonTokenType.String or System.Text.Json.JsonTokenType.PropertyName)) throw new System.Text.Json.JsonException();
        try { return r.GetString()!; }
        catch (InvalidOperationException)
        {
            // a lone surrogate escape: the reader has checked the syntax, so undo the escapes by hand
            ReadOnlySpan<byte> raw = r.HasValueSequence ? System.Buffers.BuffersExtensions.ToArray(r.ValueSequence) : r.ValueSpan;
            var sb = new StringBuilder(raw.Length);
            int i = 0;
            while (i < raw.Length)
            {
                int j = raw[i..].IndexOf((byte)'\\');
                if (j < 0) j = raw.Length - i;
                sb.Append(Encoding.UTF8.GetString(raw.Slice(i, j)));
                if ((i += j) >= raw.Length) break;
                char e = (char)raw[i + 1];
                if (e == 'u') { sb.Append((char)Convert.ToInt32(Encoding.ASCII.GetString(raw.Slice(i + 2, 4)), 16)); i += 6; }
                else { sb.Append(e switch { 'b' => '\b', 'f' => '\f', 'n' => '\n', 'r' => '\r', 't' => '\t', _ => e }); i += 2; }
            }
            return sb.ToString();
        }
    }
}
