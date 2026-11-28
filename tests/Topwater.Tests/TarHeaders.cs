using System.Text;

namespace Topwater.Tests;

/// <summary>Hand-made tar pieces for hostile header tests: a raw header block, and a stream that makes a huge entry's data as it is read.</summary>
static class TarHeaders
{
    /// <summary>One 512-byte header block with <paramref name="type"/> and <paramref name="size"/>; GNU magic for 'L' and 'K', ustar otherwise.</summary>
    public static byte[] Header(string name, char type, long size)
    {
        var h = new byte[512];
        void Put(int at, string s) => Encoding.ASCII.GetBytes(s).CopyTo(h, at);
        Put(0, name);
        Put(100, "0000644\0");
        Put(108, "0000000\0");
        Put(116, "0000000\0");
        Put(124, Convert.ToString(size, 8).PadLeft(11, '0') + "\0");
        Put(136, "14000000000\0");
        Put(148, "        ");
        h[156] = (byte)type;
        Put(257, type is 'L' or 'K' ? "ustar  \0" : "ustar\000");
        Put(148, Convert.ToString(h.Sum(b => b), 8).PadLeft(6, '0') + "\0 ");
        return h;
    }

    /// <summary>A forward-only stream: <paramref name="head"/>, then <paramref name="fill"/> bytes of 'a', then <paramref name="tail"/>. Counts what was read.</summary>
    public sealed class Generated(byte[] head, long fill, byte[] tail) : Stream
    {
        long _pos;
        public long BytesRead => _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            long total = head.Length + fill + tail.Length;
            int n = (int)Math.Min(count, total - _pos);
            for (int i = 0; i < n; i++, _pos++)
                buffer[offset + i] = _pos < head.Length ? head[_pos] : _pos < head.Length + fill ? (byte)'a' : tail[_pos - head.Length - fill];
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A tar whose first entry is a <paramref name="type"/> header ('L', 'K' or 'x') holding <paramref name="size"/> bytes, then <paramref name="rest"/>.</summary>
    public static Generated Huge(char type, long size, byte[] rest)
    {
        // 'x': one record "<size> path=aaa...a\n" (its length counts its own digits); 'L' and 'K': a name of 'a's and a NUL
        var prefix = type == 'x' ? Encoding.ASCII.GetBytes($"{size} path=") : [];
        byte[] end = type == 'x' ? [(byte)'\n'] : [0];
        var head = Header(type == 'x' ? "PaxHeaders/f" : "././@LongLink", type, size).Concat(prefix).ToArray();
        var tail = end.Concat(new byte[(512 - size % 512) % 512]).Concat(rest).ToArray();
        return new Generated(head, size - prefix.Length - end.Length, tail);
    }
}
