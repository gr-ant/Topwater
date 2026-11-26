using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using static Topwater.GitCore;

namespace Topwater;

/// <summary>A broken pack entry; the caller names the object it was looking for.</summary>
internal sealed class GitPackCorrupt : Exception;

/// <summary>Reading git packfiles (.git/objects/pack/*.pack with their v2 .idx), as `git clone` and `git gc` leave them.
/// Read only: new objects are still written loose. A pack is agent-controlled input, so both checksums are verified on
/// load, every offset and size is checked, delta chains are bounded and walked without recursion, and inflated sizes stay
/// under the shell's string limit. A port of python/src/topwater/commands/gitpack.py.</summary>
internal sealed class GitPack
{
    public const int MaxChain = 4096;  // deltas on top of one base (real git refuses to write deeper than 4095)
    public const int MaxPacks = 256;
    const long CacheBytes = 32 << 20;  // inflated objects kept for reuse as delta bases
    static readonly string?[] Kinds = [null, "commit", "tree", "blob", "tag"];

    readonly byte[] _pack;
    readonly long _limit;
    readonly Action _check;
    readonly Dictionary<long, (string, byte[])> _cache = [];
    long _cached;
    readonly string[] _names;
    public readonly Dictionary<string, long> Offsets = new(StringComparer.Ordinal);

    static GitException BadPack(string path, string why, params (string, string)[] values) =>
        Fatal("badPack", ("path", path), ("why", Msg(why, values)));

    public GitPack(string path, byte[] idx, byte[] pack, long limit, Action check)
    {
        _pack = pack; _limit = limit; _check = check;
        var ipath = path[..^5] + ".idx";
        if (idx.Length < 1072 || idx[0] != 0xff || idx[1] != (byte)'t' || idx[2] != (byte)'O' || idx[3] != (byte)'c')
            throw BadPack(ipath, "packBadSignature");
        uint iver = BinaryPrimitives.ReadUInt32BigEndian(idx.AsSpan(4));
        if (iver != 2) throw BadPack(ipath, "packBadVersion", ("version", iver.ToString()));
        var fan = new long[256];
        for (int b = 0; b < 256; b++) fan[b] = BinaryPrimitives.ReadUInt32BigEndian(idx.AsSpan(8 + 4 * b));
        long n = fan[255];
        long large = idx.Length - 1072L - 28L * n;
        bool mono = true;
        for (int b = 0; b < 255; b++) if (fan[b] > fan[b + 1]) mono = false;
        if (!mono || large < 0 || large % 8 != 0) throw BadPack(ipath, "packTruncated");
        if (!SHA1.HashData(idx.AsSpan(0, idx.Length - 20)).AsSpan().SequenceEqual(idx.AsSpan(idx.Length - 20)))
            throw BadPack(ipath, "packBadChecksum");
        if (pack.Length < 32 || pack[0] != (byte)'P' || pack[1] != (byte)'A' || pack[2] != (byte)'C' || pack[3] != (byte)'K')
            throw BadPack(path, "packBadSignature");
        uint version = BinaryPrimitives.ReadUInt32BigEndian(pack.AsSpan(4)), count = BinaryPrimitives.ReadUInt32BigEndian(pack.AsSpan(8));
        if (version is not (2 or 3)) throw BadPack(path, "packBadVersion", ("version", version.ToString()));
        if (!SHA1.HashData(pack.AsSpan(0, pack.Length - 20)).AsSpan().SequenceEqual(pack.AsSpan(pack.Length - 20)))
            throw BadPack(path, "packBadChecksum");
        if (count != n || !idx.AsSpan(idx.Length - 40, 20).SequenceEqual(pack.AsSpan(pack.Length - 20)))
            throw BadPack(path, "packNoMatch");
        int count32 = (int)n;
        _names = new string[count32];
        long end = pack.Length - 20, bigCount = large / 8;
        int smallAt = 1032 + 24 * count32, bigAt = 1032 + 28 * count32;
        for (int i = 0; i < count32; i++)
        {
            if ((i & 1023) == 0) _check();
            var name = Convert.ToHexString(idx, 1032 + 20 * i, 20).ToLowerInvariant();
            _names[i] = name;
            int first = idx[1032 + 20 * i];
            if ((i > 0 && string.CompareOrdinal(name, _names[i - 1]) <= 0) || fan[first] <= i || (first > 0 && fan[first - 1] > i))
                throw BadPack(ipath, "packNotSorted");
            long off = BinaryPrimitives.ReadUInt32BigEndian(idx.AsSpan(smallAt + 4 * i));
            if ((off & 0x80000000) != 0)
            {
                long k = off & 0x7FFFFFFF;
                if (k >= bigCount) throw BadPack(ipath, "packBadOffset");
                ulong big = BinaryPrimitives.ReadUInt64BigEndian(idx.AsSpan(bigAt + 8 * (int)k));
                off = big > long.MaxValue ? -1 : (long)big;
            }
            if (off < 12 || off >= end) throw BadPack(ipath, "packBadOffset");
            Offsets[name] = off;
        }
    }

    public List<string> WithPrefix(string prefix)
    {
        int i = Array.BinarySearch(_names, prefix, StringComparer.Ordinal);
        if (i < 0) i = ~i;
        var output = new List<string>();
        while (i < _names.Length && _names[i].StartsWith(prefix, StringComparison.Ordinal) && output.Count < 2) output.Add(_names[i++]);
        return output;
    }

    /// <summary>The object's kind and contents; GitPackCorrupt for a broken entry (the caller still verifies the id).</summary>
    public (string Kind, byte[] Data) Read(string sha)
    {
        try { return Unpack(Offsets[sha]); }
        catch (IndexOutOfRangeException) { throw new GitPackCorrupt(); }
        catch (ArgumentException) { throw new GitPackCorrupt(); }
        catch (InvalidDataException) { throw new GitPackCorrupt(); }
    }

    (string, byte[]) Unpack(long off)
    {
        var p = _pack;
        var chain = new List<(long Entry, long Pos, long Size)>();  // newest first
        var seen = new HashSet<long>();
        string kind;
        byte[] data;
        while (true)
        {
            _check();
            if (_cache.TryGetValue(off, out var hit)) { (kind, data) = hit; break; }
            if (!seen.Add(off) || chain.Count >= MaxChain) throw new GitPackCorrupt();
            long pos = off;
            int c = p[pos++];
            int typ = (c >> 4) & 7, shift = 4;
            long size = c & 15;
            while ((c & 0x80) != 0)
            {
                c = p[pos++];
                if (shift > 56) throw new GitPackCorrupt();
                size |= (long)(c & 0x7F) << shift;
                shift += 7;
            }
            if (size < 0 || size > _limit) throw new GitPackCorrupt();
            long baseOff;
            if (typ is >= 1 and <= 4)
            {
                kind = Kinds[typ]!;
                data = Inflate(pos, size);
                Remember(off, kind, data);
                break;
            }
            if (typ == 6)  // offset delta: the base is earlier in this pack
            {
                c = p[pos++];
                long back = c & 0x7F;
                while ((c & 0x80) != 0)
                {
                    c = p[pos++];
                    back = ((back + 1) << 7) | (long)(c & 0x7F);
                    if (back > off) throw new GitPackCorrupt();
                }
                baseOff = off - back;
                if (baseOff < 12 || baseOff >= off) throw new GitPackCorrupt();
            }
            else if (typ == 7)  // reference delta: the base is named by its id (in this pack)
            {
                if (pos + 20 > p.Length) throw new GitPackCorrupt();
                if (!Offsets.TryGetValue(Convert.ToHexString(p, (int)pos, 20).ToLowerInvariant(), out baseOff)) throw new GitPackCorrupt();
                pos += 20;
            }
            else throw new GitPackCorrupt();
            chain.Add((off, pos, size));
            off = baseOff;
        }
        for (int i = chain.Count - 1; i >= 0; i--)
        {
            var (entry, pos, size) = chain[i];
            data = ApplyDelta(data, Inflate(pos, size), _limit, _check);
            Remember(entry, kind, data);
        }
        return (kind, data);
    }

    byte[] Inflate(long pos, long size)
    {
        long end = _pack.Length - 20;
        if (pos >= end) throw new GitPackCorrupt();
        using var z = new ZLibStream(new MemoryStream(_pack, (int)pos, (int)(end - pos), false), CompressionMode.Decompress);
        var ms = new MemoryStream();
        var buf = new byte[65536];
        while (true)
        {
            _check();
            // never more than one byte past the stated size
            int want = (int)Math.Min(buf.Length, size + 1 - ms.Length);
            if (want <= 0) throw new GitPackCorrupt();
            int got = z.Read(buf, 0, want);
            if (got == 0) break;
            ms.Write(buf, 0, got);
        }
        if (ms.Length != size) throw new GitPackCorrupt();
        return ms.ToArray();
    }

    void Remember(long off, string kind, byte[] data)
    {
        if (_cached + data.Length > CacheBytes) { _cache.Clear(); _cached = 0; }
        if (data.Length <= CacheBytes) { _cache[off] = (kind, data); _cached += data.Length; }
    }

    static long Varint(byte[] d, ref int i)
    {
        long v = 0;
        int shift = 0;
        while (true)
        {
            int c = d[i++];
            if (shift > 56) throw new GitPackCorrupt();
            v |= (long)(c & 0x7F) << shift;
            shift += 7;
            if ((c & 0x80) == 0) return v;
        }
    }

    /// <summary>Rebuilds an object from its base and a delta: a header (base size, result size), then copy and insert steps.</summary>
    public static byte[] ApplyDelta(byte[] baseData, byte[] delta, long limit, Action check)
    {
        int i = 0;
        long src = Varint(delta, ref i), dst = Varint(delta, ref i);
        if (src != baseData.Length || dst < 0 || dst > limit) throw new GitPackCorrupt();
        var output = new byte[dst];
        long len = 0, steps = 0;
        int n = delta.Length;
        while (i < n)
        {
            if ((++steps & 0xFFF) == 0) check();
            int c = delta[i++];
            if ((c & 0x80) != 0)  // copy from the base: offset and size bytes present per bit
            {
                long off = 0, size = 0;
                for (int k = 0; k < 4; k++) if ((c & (1 << k)) != 0) off |= (long)delta[i++] << (8 * k);
                for (int k = 0; k < 3; k++) if ((c & (0x10 << k)) != 0) size |= (long)delta[i++] << (8 * k);
                if (size == 0) size = 0x10000;
                if (off + size > baseData.Length || len + size > dst) throw new GitPackCorrupt();
                Array.Copy(baseData, off, output, len, size);
                len += size;
            }
            else if (c != 0)  // insert the next c bytes of the delta
            {
                if (i + c > n || len + c > dst) throw new GitPackCorrupt();
                Array.Copy(delta, i, output, len, c);
                i += c;
                len += c;
            }
            else throw new GitPackCorrupt();
        }
        if (len != dst) throw new GitPackCorrupt();
        return output;
    }
}
