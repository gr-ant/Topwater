// WASI preview1 over the shell's virtual filesystem, for the python3 command (PythonCommand.cs).
//
// The guest is bashkit's CPython build (assets/python-wasm). It reaches only what this file hands it: the virtual filesystem
// as the agent's user, the command's stdin and output, a clock, random bytes, and HTTP through a callback the command
// supplies. There is no host filesystem, socket, process or thread API: sockets answer ENOTSUP. Wasmtime's own WASI is never
// used (it maps real directories). See docs/python.md; python/src/topwater/wasi.py is the reference this mirrors.
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using Wasmtime;

namespace Topwater.Python;

/// <summary>The add-on can't run: its interpreter files are missing or altered, or the compile failed.</summary>
public sealed class PythonUnavailableException(string message) : Exception(message);

internal static class Wasi
{
    // ---------- artifacts and runtime ----------

    // assets/python-wasm/MANIFEST pins python.wasm.xz; this add-on carries the same module gzipped (tools/python-wasm), so it
    // checks the decompressed module, whose hash the design note gives
    internal const string ModuleSha256 = "71df1f2cf7b3dbc0ee56b7fc58243f6ae17d9d57cdbb50855598241a7a357600";
    internal const string StdlibSha256 = "a2fbfc15b11fe851fffe3a84369f4d8c73992904ea23bbc045d89373737e15ea";
    internal const string ModuleFile = "python.wasm.gz";
    internal const string StdlibFile = "python314.zip";
    internal const string StdlibPath = "/usr/local/lib/python314.zip"; // where the snapshot expects the stdlib; served from memory, read-only
    // CPython recurses in C (parser, repr, nested containers): the default 512 KB overflows before RecursionError. 2 MB is the
    // most Wasmtime's C API accepts (its async stack size, which .NET can't set); bashkit uses 4 MB.
    const int MaxWasmStack = 2 << 20;
    internal const int ThreadStack = 32 << 20; // the native stack of the thread the guest runs on: wasm's stack plus the host's frames
    static readonly TimeSpan EpochTick = TimeSpan.FromMilliseconds(10); // how late a timed-out guest may notice

    /// <summary>One compiled interpreter per process and artifacts: the engine, the module, and a linker whose functions serve the
    /// guest of the thread that calls them. Each call gets a fresh store and instance.</summary>
    internal sealed class Runtime
    {
        public readonly Engine Engine;
        public readonly Module Module;
        public readonly Linker Linker;
        public readonly byte[] Stdlib;
        int _calls;
        readonly object _lock = new();

        public Runtime(string? folder, string? cacheDir)
        {
            var gz = Load(folder, ModuleFile);
            Stdlib = Load(folder, StdlibFile);
            if (Sha256(Stdlib) != StdlibSha256) throw new PythonUnavailableException($"{StdlibFile}: not the expected file (its sha256 differs)");
            byte[] wasm;
            try
            {
                using var unzip = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
                using var raw = new MemoryStream();
                unzip.CopyTo(raw);
                wasm = raw.ToArray();
            }
            catch (InvalidDataException) { wasm = []; }
            if (Sha256(wasm) != ModuleSha256) throw new PythonUnavailableException($"{ModuleFile}: not the expected file (its sha256 differs)");

            Engine = new Engine(new Config().WithEpochInterruption(true).WithMaximumStackSize(MaxWasmStack));
            var version = typeof(Engine).Assembly.GetName().Version?.ToString() ?? "unknown";
            var cached = cacheDir is null ? null : Path.Combine(cacheDir, $"python-{ModuleSha256[..16]}-wasmtime-{version}.cwasm");
            Module? module = null;
            if (cached is not null && File.Exists(cached))
            {
                try { module = Module.DeserializeFile(Engine, "python", cached); } // the host's own cache directory: as trusted as the host's code
                catch (WasmtimeException) { module = null; }
            }
            if (module is null)
            {
                module = Module.FromBytes(Engine, "python", wasm);
                if (cached is not null)
                {
                    Directory.CreateDirectory(cacheDir!);
                    var tmp = $"{cached}.{Environment.ProcessId}.tmp";
                    File.WriteAllBytes(tmp, module.Serialize());
                    File.Move(tmp, cached, overwrite: true);
                }
            }
            Module = module;
            Linker = new Linker(Engine);
            Define(Linker);
        }

        /// <summary>Keeps the epoch ticking while a call runs; stops when none does. Shared by every call, so never per call.</summary>
        public IDisposable Ticking()
        {
            lock (_lock)
                if (++_calls == 1) new Thread(Tick) { IsBackground = true, Name = "topwater-python-epoch" }.Start();
            return new Done(() => { lock (_lock) _calls--; });
        }

        void Tick()
        {
            while (true)
            {
                Thread.Sleep(EpochTick);
                lock (_lock) if (_calls == 0) return;
                Engine.IncrementEpoch();
            }
        }

        sealed class Done(Action a) : IDisposable { public void Dispose() => a(); }
    }

    static readonly Dictionary<(string?, string?), Runtime> Runtimes = [];

    /// <summary>The process's compiled interpreter for these artifacts: the first call compiles it (seconds), later calls reuse it.</summary>
    internal static Runtime GetRuntime(string? folder, string? cacheDir)
    {
        var key = (folder is null ? null : Path.GetFullPath(folder), cacheDir);
        lock (Runtimes)
        {
            if (!Runtimes.TryGetValue(key, out var rt)) Runtimes[key] = rt = new Runtime(key.Item1, cacheDir);
            return rt;
        }
    }

    /// <summary>Forgets compiled interpreters (tests of the compile cache).</summary>
    internal static void ForgetRuntimes() { lock (Runtimes) Runtimes.Clear(); }

    /// <summary>A file from the host's artifacts folder, or the one this assembly carries.</summary>
    static byte[] Load(string? folder, string name)
    {
        if (folder is not null)
        {
            try { return File.ReadAllBytes(Path.Combine(folder, name)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new PythonUnavailableException($"{name}: {e.Message}"); }
        }
        using var s = typeof(Wasi).Assembly.GetManifestResourceStream(name) ?? throw new PythonUnavailableException($"{name}: missing from the add-on");
        using var m = new MemoryStream();
        s.CopyTo(m);
        return m.ToArray();
    }

    static string Sha256(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    // ---------- guest state ----------

    // WASI preview1 errno values (not Linux's)
    internal const int SUCCESS = 0, E2BIG = 1, EACCES = 2, EBADF = 8, EEXIST = 20, EFAULT = 21, EFBIG = 22, EINVAL = 28, EIO = 29, EISDIR = 31, ELOOP = 32, EMFILE = 33;
    internal const int ENAMETOOLONG = 37, ENOENT = 44, ENOSPC = 51, ENOTDIR = 54, ENOTEMPTY = 55, ENOTSUP = 58, EPERM = 63, EROFS = 69, ESPIPE = 70;
    static readonly Dictionary<string, int> ByText = new(StringComparer.Ordinal)
    {
        [CommandSpec.Errno.ENOENT] = ENOENT, [CommandSpec.Errno.EACCES] = EACCES, [CommandSpec.Errno.EPERM] = EPERM, [CommandSpec.Errno.ENOTDIR] = ENOTDIR,
        [CommandSpec.Errno.EISDIR] = EISDIR, [CommandSpec.Errno.EEXIST] = EEXIST, [CommandSpec.Errno.ENOTEMPTY] = ENOTEMPTY, [CommandSpec.Errno.EINVAL] = EINVAL,
        [CommandSpec.Errno.ENOSPC] = ENOSPC, [CommandSpec.Errno.EBADF] = EBADF, [CommandSpec.Errno.ELOOP] = ELOOP,
    };
    static readonly Dictionary<int, string> Text = ByText.ToDictionary(kv => kv.Value, kv => kv.Key);

    const byte FILETYPE_UNKNOWN = 0, FILETYPE_DIRECTORY = 3, FILETYPE_REGULAR_FILE = 4, FILETYPE_SYMBOLIC_LINK = 7;
    const int OFLAGS_CREAT = 1, OFLAGS_DIRECTORY = 2, OFLAGS_EXCL = 4, OFLAGS_TRUNC = 8;
    const int FDFLAGS_APPEND = 1;
    const int LOOKUP_SYMLINK_FOLLOW = 1;
    const long RIGHTS_FD_READ = 1 << 1, RIGHTS_FD_WRITE = 1 << 6, RIGHTS_FD_READDIR = 1 << 14, RIGHTS_ALL = (1 << 30) - 1;
    const int FSTFLAGS_MTIM = 1 << 2, FSTFLAGS_MTIM_NOW = 1 << 3;
    const int CLOCK_REALTIME = 0, CLOCK_MONOTONIC = 1;
    const byte EVENTTYPE_CLOCK = 0, EVENTTYPE_FD_READ = 1, EVENTTYPE_FD_WRITE = 2;
    const int SUBCLOCKFLAGS_ABSTIME = 1;
    const int ROOT_FD = 3; // the one preopen, "/": the snapshot's wasi-libc cached exactly this table
    const int MAX_FDS = 1024;
    const int MAX_PATH = 4096;
    const int MAX_IOVS = 1024;
    const int MAX_RANDOM = 1 << 20;
    static readonly TimeSpan MaxSleepSlice = TimeSpan.FromMilliseconds(250);

    internal sealed class WasiException(int errno) : Exception(errno.ToString()) { public int Errno { get; } = errno; }

    /// <summary>proc_exit: the guest ends the call with this status.</summary>
    internal sealed class ProcExit(int code) : Exception("proc_exit") { public int Code { get; } = code; }

    /// <summary>The call's deadline passed (a sleep reached it, or the epoch found it): the guest is stopped.</summary>
    internal sealed class Interrupted() : Exception("interrupted");

    /// <summary>stdout plus stderr passed the shell's string limit: the guest is stopped and the call ends as for any command.</summary>
    internal sealed class OutputFull() : Exception("output full");

    /// <summary>A filesystem call for the guest: its result, or WasiException with the errno. A mounted file or folder the source has
    /// not been asked for yet is fetched (the host waits, within the call's time) and the call tried once more.</summary>
    static T Fs<T>(Func<T> fn)
    {
        try { return fn(); }
        catch (ShellException e)
        {
            var g = Current;
            if (!ByText.ContainsKey(Tail(e)) && g?.Fetch is { } fetch && fetch())
            {
                try { return fn(); }
                catch (ShellException again) { throw new WasiException(ErrnoOf(again)); }
            }
            throw new WasiException(ErrnoOf(e));
        }
    }

    static void Fs(Action fn) => Fs(() => { fn(); return 0; });

    static string Tail(ShellException e) => e.Message[(e.Message.LastIndexOf(": ", StringComparison.Ordinal) is var i and >= 0 ? i + 2 : 0)..];

    /// <summary>The WASI errno for a filesystem error, by its text ("path: Permission denied"). Anything else (a mounted file not
    /// fetched yet) is EIO, and its text is kept for the command to print after the program.</summary>
    internal static int ErrnoOf(ShellException e)
    {
        if (ByText.TryGetValue(Tail(e), out var errno)) return errno;
        Current?.Notes.Add(e.Message);
        return EIO;
    }

    internal static string ErrnoText(int errno) => Text.GetValueOrDefault(errno, "Input/output error");

    internal sealed class WasiFile(string path, MemoryStream data, bool read, bool write, bool append, long stored, long mtime, bool counted = true)
    {
        public readonly string Path = path;
        public MemoryStream Data = data;
        public long Pos;
        public readonly bool Read = read, Write = write, Counted = counted; // Counted: in the buffer budget (the stdlib is not)
        public bool Append = append, Dirty;
        public long Stored = stored, Mtime = mtime;
    }

    internal sealed class WasiDir(string path)
    {
        public readonly string Path = path;
        public List<(byte[] Name, byte Kind, ulong Ino)>? Entries;
    }

    static readonly object StdinFd = "stdin", StdoutFd = "stdout", StderrFd = "stderr", RootFd = "root";

    /// <summary>Everything one python3 call's instance can reach. <see cref="Http"/>(method, url, headers, body, timeout) sends a
    /// request for the HTTP bridge.</summary>
    internal sealed class Guest
    {
        public readonly VirtualFileSystem FileSystem;
        public readonly List<byte[]> Args, Env;
        public readonly byte[] Stdin;
        public int StdinPos;
        public readonly MemoryStream Stdout = new(), Stderr = new();
        public readonly long OutputCap, MaxMemory;
        public readonly byte[] Stdlib;
        public readonly DateTime Deadline; // UTC
        public readonly CancellationToken Cancel; // `timeout` and the host's cancellation
        public readonly System.Diagnostics.Stopwatch Started = System.Diagnostics.Stopwatch.StartNew();
        public readonly List<object?> Fds = [StdinFd, StdoutFd, StderrFd, RootFd];
        public long Buffered; // bytes held in open files' buffers, at most MaxMemory in all
        public readonly Func<string, string, List<KeyValuePair<string, string>>, byte[], TimeSpan, (int Code, byte[] Payload)> Http;
        public byte[]? Pending;
        public readonly Func<bool>? Fetch; // fetches what mounts were asked for and missed; null: no mounts
        public readonly List<(string Path, int Errno)> WriteBackErrors = [];
        public readonly List<string> Notes = [];
        public Memory? Memory;

        public Guest(VirtualFileSystem fs, IEnumerable<string> args, IEnumerable<KeyValuePair<string, string>> env, byte[] stdin, long outputCap, long maxMemory,
            byte[] stdlib, DateTime deadline, CancellationToken cancel,
            Func<string, string, List<KeyValuePair<string, string>>, byte[], TimeSpan, (int, byte[])> http, Func<bool>? fetch = null)
        {
            FileSystem = fs;
            Args = [.. args.Select(a => ByteText.Encode(a.Replace("\0", "")))];
            Env = [.. env.Where(e => e.Key.Length > 0 && !e.Key.Contains('=') && !(e.Key + e.Value).Contains('\0')).Select(e => ByteText.Encode($"{e.Key}={e.Value}"))];
            Stdin = stdin;
            OutputCap = outputCap;
            MaxMemory = maxMemory;
            Stdlib = stdlib;
            Deadline = deadline;
            Cancel = cancel;
            Http = http;
            Fetch = fetch;
        }

        public bool TimedOut => Cancel.IsCancellationRequested || DateTime.UtcNow >= Deadline;

        // -- descriptors --

        public int Alloc(object entry)
        {
            for (int i = ROOT_FD + 1; i < Fds.Count; i++)
                if (Fds[i] is null) { Fds[i] = entry; return i; }
            if (Fds.Count >= MAX_FDS) throw new WasiException(EMFILE);
            Fds.Add(entry);
            return Fds.Count - 1;
        }

        public object Fd(int fd) => fd >= 0 && fd < Fds.Count && Fds[fd] is { } e ? e : throw new WasiException(EBADF);

        /// <summary>The absolute path for a guest path relative to a directory descriptor; ".." stops at "/" (the whole sandbox).</summary>
        public string Resolve(int dirfd, byte[] raw)
        {
            if (raw.Length == 0) throw new WasiException(ENOENT);
            if (raw.Length > MAX_PATH) throw new WasiException(ENAMETOOLONG);
            if (Array.IndexOf(raw, (byte)0) >= 0) throw new WasiException(EINVAL);
            var d = Fd(dirfd);
            var dbase = d == RootFd ? "/" : d is WasiDir dir ? dir.Path : throw new WasiException(ENOTDIR);
            return FileSystem.NormalizePhysical(dbase, ByteText.Decode(raw));
        }

        // -- files --

        public void Reserve(long extra)
        {
            if (Buffered + extra > MaxMemory) throw new WasiException(ENOSPC);
            Buffered += extra;
        }

        /// <summary>How large the file may grow: what the filesystem has free, plus what it holds of this file already.</summary>
        public long Room(WasiFile f) => FileSystem.RunAs is not null ? FileSystem.Capacity - FileSystem.UsedBytes + f.Stored : 1L << 62;

        public void Save(WasiFile f)
        {
            if (!f.Dirty) return;
            Fs(() => FileSystem.WriteAllBytes(f.Path, f.Data.ToArray()));
            f.Dirty = false;
            f.Stored = f.Data.Length;
        }

        /// <summary>At exit: files the program wrote but never closed are saved, as a real process's are.</summary>
        public void FlushAll()
        {
            foreach (var entry in Fds)
                if (entry is WasiFile { Dirty: true } f)
                {
                    try { Save(f); }
                    catch (WasiException e) { WriteBackErrors.Add((f.Path, e.Errno)); }
                }
        }

        public void WriteOutput(MemoryStream which, byte[] data)
        {
            if (Stdout.Length + Stderr.Length + data.Length > OutputCap) throw new OutputFull();
            which.Write(data);
        }
    }

    // ---------- wasi functions ----------

    [ThreadStatic] internal static Guest? t_guest; // the guest of the call running on this thread: the linker is shared by every call
    static Guest? Current => t_guest;
    static Guest G => t_guest!;

    static Memory Mem() => G.Memory ?? throw new WasiException(EFAULT);

    static void Check(int ptr, int n)
    {
        if ((long)(uint)ptr + (uint)n > Mem().GetLength()) throw new WasiException(EFAULT);
    }

    static byte[] Read(int ptr, int n)
    {
        Check(ptr, n);
        return n == 0 ? [] : Mem().GetSpan((uint)ptr, (int)(uint)n).ToArray();
    }

    static void Write(int ptr, ReadOnlySpan<byte> data)
    {
        var mem = Mem();
        long p = (uint)ptr;
        if (p + data.Length > mem.GetLength()) throw new WasiException(EFAULT);
        if (data.Length > 0) data.CopyTo(mem.GetSpan(p, data.Length));
    }

    static void U32(int ptr, long v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v);
        Write(ptr, b);
    }

    static void U64(int ptr, long v)
    {
        Span<byte> b = stackalloc byte[8];
        BinaryPrimitives.WriteInt64LittleEndian(b, v);
        Write(ptr, b);
    }

    static byte[] PathArg(int ptr, int n)
    {
        if ((uint)n > MAX_PATH) throw new WasiException(ENAMETOOLONG);
        return Read(ptr, n);
    }

    static List<(int Ptr, int Len)> Iovs(int ptr, int count)
    {
        if (count < 0 || count > MAX_IOVS) throw new WasiException(EINVAL);
        var raw = Read(ptr, count * 8);
        var list = new List<(int, int)>(count);
        for (int i = 0; i < count; i++) list.Add((BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * 8)), BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(i * 8 + 4))));
        return list;
    }

    static void StringTable(List<byte[]> items, int ptrs, int buf)
    {
        var offsets = new byte[items.Count * 4];
        var blob = new MemoryStream();
        long at = (uint)buf;
        for (int i = 0; i < items.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(offsets.AsSpan(i * 4), (uint)(at + blob.Length));
            blob.Write(items[i]);
            blob.WriteByte(0);
        }
        Write(ptrs, offsets);
        Write(buf, blob.ToArray());
    }

    // shortcut: a SHA-256 prefix, where the Python port uses blake2b-8; inode numbers differ between ports (each is stable)
    static ulong Ino(string path) => BinaryPrimitives.ReadUInt64LittleEndian(SHA256.HashData(ByteText.Encode(path))) | 1;

    static byte[] Filestat(byte filetype, long size, long mtime, ulong ino, long nlink = 1)
    {
        var b = new byte[64];
        BinaryPrimitives.WriteUInt64LittleEndian(b, 1);
        BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(8), ino);
        b[16] = filetype;
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(24), nlink);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(32), size);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(40), mtime);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(48), mtime);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(56), mtime);
        return b;
    }

    static long Ns(DateTime when) => (DateTime.SpecifyKind(when, DateTimeKind.Utc) - DateTime.UnixEpoch).Ticks * 100;

    static long NowNs(Guest g) => Ns(g.FileSystem.Clock()); // the machine's clock: virtual when the shell has one

    static byte[] StatPath(Guest g, string path, bool follow)
    {
        if (path == StdlibPath) return Filestat(FILETYPE_REGULAR_FILE, g.Stdlib.Length, 0, Ino(path));
        var st = Fs(() => g.FileSystem.Stat(path, follow));
        var kind = st.IsSymlink ? FILETYPE_SYMBOLIC_LINK : st.IsDirectory ? FILETYPE_DIRECTORY : FILETYPE_REGULAR_FILE;
        return Filestat(kind, st.Size, Ns(st.Modified), Ino(path), st.Links);
    }

    /// <summary>A WASI function: WasiException becomes its errno, success 0. Exits, timeouts and full output pass through.</summary>
    static int E(Action fn)
    {
        try { fn(); return SUCCESS; }
        catch (WasiException e) { return e.Errno; }
    }

    static void ArgsSizesGet(int argc, int size)
    {
        U32(argc, G.Args.Count);
        U32(size, G.Args.Sum(a => a.Length + 1));
    }

    static void EnvironSizesGet(int count, int size)
    {
        U32(count, G.Env.Count);
        U32(size, G.Env.Sum(e => e.Length + 1));
    }

    static void ClockTimeGet(int clock, int @out)
    {
        long now = clock switch
        {
            CLOCK_REALTIME => NowNs(G),
            CLOCK_MONOTONIC => MonotonicNs(),
            _ => G.Started.Elapsed.Ticks * 100, // process and thread CPU time: time since this call started
        };
        U64(@out, now);
    }

    static long MonotonicNs() => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * (1e9 / System.Diagnostics.Stopwatch.Frequency));

    static void RandomGet(int buf, int n)
    {
        if (n < 0 || n > MAX_RANDOM) throw new WasiException(EINVAL);
        Write(buf, RandomNumberGenerator.GetBytes(n));
    }

    static void FdPrestatGet(int fd, int buf)
    {
        if (G.Fd(fd) != RootFd) throw new WasiException(EBADF);
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4), 1);
        Write(buf, b);
    }

    static void FdPrestatDirName(int fd, int buf, int n)
    {
        if (G.Fd(fd) != RootFd) throw new WasiException(EBADF);
        if (n < 1) throw new WasiException(EINVAL);
        Write(buf, "/"u8);
    }

    static void FdFdstatGet(int fd, int buf)
    {
        var e = G.Fd(fd);
        (byte kind, int flags, long rights) = e == StdinFd ? (FILETYPE_UNKNOWN, 0, RIGHTS_FD_READ) // pipes, not terminals: isatty() is false
            : e == StdoutFd || e == StderrFd ? (FILETYPE_UNKNOWN, 0, RIGHTS_FD_WRITE)
            : e is WasiFile f ? (FILETYPE_REGULAR_FILE, f.Append ? FDFLAGS_APPEND : 0, RIGHTS_ALL)
            : (FILETYPE_DIRECTORY, 0, RIGHTS_ALL);
        var b = new byte[24];
        b[0] = kind;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(2), (ushort)flags);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(8), rights);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(16), rights);
        Write(buf, b);
    }

    static void FdFdstatSetFlags(int fd, int flags)
    {
        if (G.Fd(fd) is WasiFile f) f.Append = (flags & FDFLAGS_APPEND) != 0;
    }

    static void FdFilestatGet(int fd, int buf)
    {
        var g = G;
        var e = g.Fd(fd);
        var data = e is WasiFile f ? Filestat(FILETYPE_REGULAR_FILE, f.Data.Length, f.Mtime, Ino(f.Path))
            : e == RootFd ? Filestat(FILETYPE_DIRECTORY, 4096, 0, Ino("/"))
            : e is WasiDir d ? StatPath(g, d.Path, true)
            : Filestat(FILETYPE_UNKNOWN, 0, 0, (ulong)fd + 1);
        Write(buf, data);
    }

    static void FdFilestatSetSize(int fd, long size)
    {
        var g = G;
        if (g.Fd(fd) is not WasiFile f) throw new WasiException(EINVAL);
        if (!f.Write) throw new WasiException(EBADF);
        if (size < 0) throw new WasiException(EINVAL);
        if (size > g.Room(f)) throw new WasiException(EFBIG);
        long current = f.Data.Length;
        g.Reserve(Math.Max(0, size - current));
        g.Buffered -= Math.Max(0, current - size);
        f.Data.SetLength(size); // extending fills with zeros
        f.Dirty = true;
    }

    static int ReadInto(int fd, int iovsPtr, int n, long? offset)
    {
        var g = G;
        var iovs = Iovs(iovsPtr, n);
        long want = iovs.Sum(v => (long)(uint)v.Len);
        var e = g.Fd(fd);
        byte[] chunk;
        if (e == StdinFd)
        {
            if (offset is not null) throw new WasiException(ESPIPE);
            int take = (int)Math.Min(want, g.Stdin.Length - g.StdinPos);
            chunk = g.Stdin.AsSpan(g.StdinPos, take).ToArray();
            g.StdinPos += take;
        }
        else if (e is WasiFile f)
        {
            if (!f.Read) throw new WasiException(EBADF);
            long start = offset ?? f.Pos;
            int take = (int)Math.Max(0, Math.Min(want, f.Data.Length - start));
            chunk = new byte[take];
            if (take > 0)
            {
                f.Data.Position = start;
                f.Data.ReadExactly(chunk);
            }
            if (offset is null) f.Pos = start + take;
        }
        else if (e == StdoutFd || e == StderrFd) throw new WasiException(EBADF);
        else throw new WasiException(EISDIR);
        int done = 0;
        foreach (var (ptr, length) in iovs)
        {
            if (done >= chunk.Length) break;
            int piece = (int)Math.Min((uint)length, chunk.Length - done);
            Write(ptr, chunk.AsSpan(done, piece));
            done += piece;
        }
        return done;
    }

    internal static int WriteFrom(int fd, int iovsPtr, int n, long? offset)
    {
        var g = G;
        var iovs = Iovs(iovsPtr, n);
        foreach (var (ptr, length) in iovs) Check(ptr, length); // every range checked before anything changes
        long total = iovs.Sum(v => (long)(uint)v.Len);
        // copied only once the destination has room: 1024 vectors over the whole memory would be terabytes
        var e = g.Fd(fd);
        if (e == StdoutFd || e == StderrFd)
        {
            if (offset is not null) throw new WasiException(ESPIPE);
            if (g.Stdout.Length + g.Stderr.Length + total > g.OutputCap) throw new OutputFull();
            var data = new MemoryStream((int)total);
            foreach (var (ptr, length) in iovs) data.Write(Read(ptr, length));
            g.WriteOutput(e == StdoutFd ? g.Stdout : g.Stderr, data.ToArray());
            return (int)total;
        }
        if (e is not WasiFile f) throw new WasiException(e == RootFd || e is WasiDir ? EISDIR : EBADF);
        if (!f.Write) throw new WasiException(EBADF);
        long start = f.Append ? f.Data.Length : offset ?? f.Pos;
        long end = start + total;
        if (end > f.Data.Length)
        {
            if (end > g.Room(f)) throw new WasiException(ENOSPC);
            g.Reserve(end - f.Data.Length);
        }
        f.Data.Position = start; // past the end: the gap fills with zeros
        foreach (var (ptr, length) in iovs) f.Data.Write(Read(ptr, length));
        f.Dirty = true;
        f.Mtime = NowNs(g);
        if (offset is null) f.Pos = end;
        return (int)total;
    }

    static void FdSeek(int fd, long offset, int whence, int @out)
    {
        var e = G.Fd(fd);
        if (e == StdinFd || e == StdoutFd || e == StderrFd) throw new WasiException(ESPIPE);
        if (e is not WasiFile f) throw new WasiException(EBADF);
        long? origin = whence switch { 0 => 0, 1 => f.Pos, 2 => f.Data.Length, _ => null };
        if (origin is not { } o || o + offset < 0) throw new WasiException(EINVAL);
        f.Pos = o + offset;
        U64(@out, f.Pos);
    }

    static void FdTell(int fd, int @out)
    {
        var e = G.Fd(fd);
        if (e == StdinFd || e == StdoutFd || e == StderrFd) throw new WasiException(ESPIPE);
        if (e is not WasiFile f) throw new WasiException(EBADF);
        U64(@out, f.Pos);
    }

    static void FdClose(int fd)
    {
        var g = G;
        var e = g.Fd(fd);
        g.Fds[fd] = null;
        if (e is WasiFile f)
        {
            if (f.Counted) g.Buffered -= f.Data.Length;
            g.Save(f);
        }
    }

    static void FdSync(int fd)
    {
        var g = G;
        if (g.Fd(fd) is WasiFile f) g.Save(f);
    }

    static List<(byte[] Name, byte Kind, ulong Ino)> Entries(Guest g, object d)
    {
        var path = d == RootFd ? "/" : ((WasiDir)d).Path;
        var output = new List<(byte[], byte, ulong)> { ("."u8.ToArray(), FILETYPE_DIRECTORY, Ino(path)), (".."u8.ToArray(), FILETYPE_DIRECTORY, Ino(VirtualFileSystem.GetParent(path))) };
        var names = Fs(() => g.FileSystem.List(path)).ToList();
        var stdlibName = StdlibPath[(StdlibPath.LastIndexOf('/') + 1)..];
        if (path == VirtualFileSystem.GetParent(StdlibPath) && !names.Contains(stdlibName))
        {
            names.Add(stdlibName);
            names.Sort(StringComparer.Ordinal);
        }
        foreach (var name in names)
        {
            var child = path.TrimEnd('/') + "/" + name;
            byte kind;
            if (child == StdlibPath) kind = FILETYPE_REGULAR_FILE;
            else
            {
                try
                {
                    var st = g.FileSystem.Stat(child, follow: false);
                    kind = st.IsSymlink ? FILETYPE_SYMBOLIC_LINK : st.IsDirectory ? FILETYPE_DIRECTORY : FILETYPE_REGULAR_FILE;
                }
                catch (ShellException) { kind = FILETYPE_UNKNOWN; }
            }
            output.Add((ByteText.Encode(name), kind, Ino(child)));
        }
        return output;
    }

    static void FdReaddir(int fd, int buf, int n, long cookie, int used)
    {
        var g = G;
        var e = g.Fd(fd);
        if (e != RootFd && e is not WasiDir) throw new WasiException(ENOTDIR);
        var entries = e is WasiDir d ? d.Entries ??= Entries(g, d) : Entries(g, e);
        if (cookie < 0 || n < 0) throw new WasiException(EINVAL);
        var output = new MemoryStream();
        for (long i = cookie; i < entries.Count; i++)
        {
            if (output.Length >= n) break;
            var (name, kind, ino) = entries[(int)i];
            var head = new byte[24];
            BinaryPrimitives.WriteInt64LittleEndian(head, i + 1);
            BinaryPrimitives.WriteUInt64LittleEndian(head.AsSpan(8), ino);
            BinaryPrimitives.WriteUInt32LittleEndian(head.AsSpan(16), (uint)name.Length);
            head[20] = kind;
            output.Write(head);
            output.Write(name);
        }
        if (output.Length > n) output.SetLength(n);
        Write(buf, output.ToArray());
        U32(used, output.Length);
    }

    static void PathOpen(int dirfd, int pathPtr, int pathLen, int oflags, long rights, int fdflags, int @out)
    {
        var g = G;
        var path = g.Resolve(dirfd, PathArg(pathPtr, pathLen));
        bool wantDir = (oflags & OFLAGS_DIRECTORY) != 0, creat = (oflags & OFLAGS_CREAT) != 0, excl = (oflags & OFLAGS_EXCL) != 0, trunc = (oflags & OFLAGS_TRUNC) != 0;
        bool append = (fdflags & FDFLAGS_APPEND) != 0;
        bool write = (rights & RIGHTS_FD_WRITE) != 0 || trunc || append;
        bool read = (rights & (RIGHTS_FD_READ | RIGHTS_FD_READDIR)) != 0 || !write;
        var fs = g.FileSystem;
        if (path == StdlibPath)
        {
            if (write || creat) throw new WasiException(EROFS);
            if (wantDir) throw new WasiException(ENOTDIR);
            U32(@out, g.Alloc(new WasiFile(path, new MemoryStream(g.Stdlib, 0, g.Stdlib.Length, false, true), true, false, false, g.Stdlib.Length, 0, counted: false)));
            return;
        }
        FileStat? st = null;
        int why = 0;
        try { st = Fs(() => fs.Stat(path)); }
        catch (WasiException e) { why = e.Errno; }
        if (st is { IsDirectory: true })
        {
            if (creat && excl) throw new WasiException(EEXIST);
            if (write) throw new WasiException(EISDIR);
            U32(@out, g.Alloc(new WasiDir(path)));
            return;
        }
        if (st is { } s)
        {
            if (wantDir) throw new WasiException(ENOTDIR);
            if (creat && excl) throw new WasiException(EEXIST);
            if (write && !fs.CanWrite(path) || read && !fs.CanRead(path)) throw new WasiException(EACCES);
            MemoryStream data;
            long stored;
            if (trunc)
            {
                if (s.Size > 0) Fs(() => fs.WriteAllBytes(path, [])); // O_TRUNC empties the file now, as open(2) does
                (data, stored) = (new MemoryStream(), 0);
            }
            else
            {
                var raw = Fs(() => fs.ReadAllBytes(path));
                data = write ? new MemoryStream() : new MemoryStream(raw, 0, raw.Length, false, true);
                if (write) data.Write(raw);
                stored = raw.Length;
            }
            g.Reserve(data.Length);
            U32(@out, g.Alloc(new WasiFile(path, data, read, write, append, stored, Ns(s.Modified))));
            return;
        }
        if (why != ENOENT || !creat || wantDir) throw new WasiException(why);
        Fs(() => fs.WriteAllBytes(path, [])); // the parent must exist and be writable: the filesystem checks it as for any command
        U32(@out, g.Alloc(new WasiFile(path, new MemoryStream(), read, write, append, 0, NowNs(g))));
    }

    static void PathFilestatGet(int dirfd, int flags, int pathPtr, int pathLen, int buf)
    {
        var g = G;
        var path = g.Resolve(dirfd, PathArg(pathPtr, pathLen));
        Write(buf, StatPath(g, path, (flags & LOOKUP_SYMLINK_FOLLOW) != 0));
    }

    static void PathFilestatSetTimes(int dirfd, int flags, int pathPtr, int pathLen, long mtim, int fst)
    {
        var g = G;
        var path = g.Resolve(dirfd, PathArg(pathPtr, pathLen));
        if (path == StdlibPath) throw new WasiException(EROFS);
        DateTime? when = (fst & FSTFLAGS_MTIM_NOW) != 0 ? g.FileSystem.Clock()
            : (fst & FSTFLAGS_MTIM) != 0 ? DateTime.UnixEpoch.AddTicks(mtim / 100) : null;
        Fs(() => g.FileSystem.Stat(path));
        if (when is { } w) Fs(() => g.FileSystem.SetModifiedTime(path, w, follow: (flags & LOOKUP_SYMLINK_FOLLOW) != 0));
    }

    static void PathCreateDirectory(int dirfd, int pathPtr, int pathLen)
    {
        var g = G;
        var path = g.Resolve(dirfd, PathArg(pathPtr, pathLen));
        if (path == StdlibPath || g.FileSystem.Exists(path, follow: false)) throw new WasiException(EEXIST);
        Fs(() => g.FileSystem.CreateDirectory(path));
    }

    static void PathRemoveDirectory(int dirfd, int pathPtr, int pathLen)
    {
        var g = G;
        var path = g.Resolve(dirfd, PathArg(pathPtr, pathLen));
        if (path == "/") throw new WasiException(EACCES);
        if (!Fs(() => g.FileSystem.Stat(path, follow: false)).IsDirectory) throw new WasiException(ENOTDIR);
        Fs(() => g.FileSystem.Delete(path)); // refuses a directory with entries (ENOTEMPTY)
    }

    static void PathUnlinkFile(int dirfd, int pathPtr, int pathLen)
    {
        var g = G;
        var path = g.Resolve(dirfd, PathArg(pathPtr, pathLen));
        if (path == StdlibPath) throw new WasiException(EROFS);
        if (Fs(() => g.FileSystem.Stat(path, follow: false)).IsDirectory) throw new WasiException(EISDIR);
        Fs(() => g.FileSystem.Delete(path));
    }

    static void PathRename(int fd1, int oldPtr, int oldLen, int fd2, int newPtr, int newLen)
    {
        var g = G;
        var src = g.Resolve(fd1, PathArg(oldPtr, oldLen));
        var dst = g.Resolve(fd2, PathArg(newPtr, newLen));
        if (src == StdlibPath || dst == StdlibPath) throw new WasiException(EROFS);
        var fs = g.FileSystem;
        var s = Fs(() => fs.Stat(src, follow: false));
        FileStat? d = fs.Exists(dst, follow: false) ? Fs(() => fs.Stat(dst, follow: false)) : null;
        if (d is { IsDirectory: true }) // rename(2): a directory replaces an empty one; anything else can't
        {
            if (!s.IsDirectory) throw new WasiException(EISDIR);
            if (src == dst) return;
            Fs(() => fs.Delete(dst));
        }
        else if (d is not null && s.IsDirectory) throw new WasiException(ENOTDIR);
        Fs(() => fs.Move(src, dst));
    }

    static void PathSymlink(int oldPtr, int oldLen, int dirfd, int newPtr, int newLen)
    {
        var g = G;
        var target = ByteText.Decode(PathArg(oldPtr, oldLen));
        var link = g.Resolve(dirfd, PathArg(newPtr, newLen));
        if (link == StdlibPath) throw new WasiException(EEXIST);
        Fs(() => g.FileSystem.CreateSymlink(link, target));
    }

    static void PathReadlink(int dirfd, int pathPtr, int pathLen, int buf, int n, int used)
    {
        var g = G;
        var path = g.Resolve(dirfd, PathArg(pathPtr, pathLen));
        var target = ByteText.Encode(Fs(() => g.FileSystem.ReadLink(path)));
        var shown = target.AsSpan(0, Math.Min(target.Length, Math.Max(0, n)));
        Write(buf, shown);
        U32(used, shown.Length);
    }

    static void PathLink(int fd1, int flags, int oldPtr, int oldLen, int fd2, int newPtr, int newLen)
    {
        var g = G;
        var src = g.Resolve(fd1, PathArg(oldPtr, oldLen));
        var dst = g.Resolve(fd2, PathArg(newPtr, newLen));
        if (src == StdlibPath || dst == StdlibPath) throw new WasiException(EROFS);
        Fs(() => g.FileSystem.CreateHardLink(src, dst, (flags & LOOKUP_SYMLINK_FOLLOW) != 0));
    }

    static void PollOneoff(int subs, int events, int n, int nevents)
    {
        var g = G;
        if (n <= 0 || n > 64) throw new WasiException(EINVAL);
        var raw = Read(subs, n * 48);
        var ready = new List<(ulong Userdata, ushort Error, byte Kind, long Nbytes)>();
        (long Timeout, ulong Userdata)? sleep = null;
        for (int i = 0; i < n; i++)
        {
            var sub = raw.AsSpan(i * 48, 48);
            ulong userdata = BinaryPrimitives.ReadUInt64LittleEndian(sub);
            byte kind = sub[8];
            if (kind == EVENTTYPE_CLOCK)
            {
                int clock = BinaryPrimitives.ReadInt32LittleEndian(sub[16..]);
                long timeout = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(sub[24..]), long.MaxValue);
                int flags = BinaryPrimitives.ReadUInt16LittleEndian(sub[40..]);
                if ((flags & SUBCLOCKFLAGS_ABSTIME) != 0) timeout = Math.Max(0, timeout - (clock == CLOCK_REALTIME ? NowNs(g) : MonotonicNs()));
                if (sleep is null || timeout < sleep.Value.Timeout) sleep = (timeout, userdata);
            }
            else if (kind is EVENTTYPE_FD_READ or EVENTTYPE_FD_WRITE)
            {
                int fd = BinaryPrimitives.ReadInt32LittleEndian(sub[16..]);
                try
                {
                    var e = g.Fd(fd);
                    long nbytes = e == StdinFd ? g.Stdin.Length - g.StdinPos : e is WasiFile f ? Math.Max(0, f.Data.Length - f.Pos) : 0;
                    ready.Add((userdata, SUCCESS, kind, nbytes));
                }
                catch (WasiException err) { ready.Add((userdata, (ushort)err.Errno, kind, 0)); }
            }
            else throw new WasiException(EINVAL);
        }
        if (ready.Count == 0 && sleep is { } s)
        {
            // a real sleep, in slices, never past the call's deadline or a cancelled call (which then ends the guest)
            var until = DateTime.UtcNow + TimeSpan.FromTicks(Math.Min(s.Timeout / 100, TimeSpan.MaxValue.Ticks / 2));
            while (DateTime.UtcNow is var now && now < until)
            {
                if (g.TimedOut) throw new Interrupted();
                var left = (until < g.Deadline ? until : g.Deadline) - now;
                g.Cancel.WaitHandle.WaitOne(left < MaxSleepSlice ? left : MaxSleepSlice);
            }
            ready.Add((s.Userdata, SUCCESS, EVENTTYPE_CLOCK, 0));
        }
        var output = new byte[ready.Count * 32];
        for (int i = 0; i < ready.Count; i++)
        {
            var ev = output.AsSpan(i * 32);
            BinaryPrimitives.WriteUInt64LittleEndian(ev, ready[i].Userdata);
            BinaryPrimitives.WriteUInt16LittleEndian(ev[8..], ready[i].Error);
            ev[10] = ready[i].Kind;
            BinaryPrimitives.WriteInt64LittleEndian(ev[16..], ready[i].Nbytes);
        }
        Write(events, output);
        U32(nevents, ready.Count);
    }

    const string W = "wasi_snapshot_preview1";

    static void Define(Linker l)
    {
        l.DefineFunction(W, "args_get", (int argv, int buf) => E(() => StringTable(G.Args, argv, buf)));
        l.DefineFunction(W, "args_sizes_get", (int argc, int size) => E(() => ArgsSizesGet(argc, size)));
        l.DefineFunction(W, "environ_get", (int env, int buf) => E(() => StringTable(G.Env, env, buf)));
        l.DefineFunction(W, "environ_sizes_get", (int count, int size) => E(() => EnvironSizesGet(count, size)));
        l.DefineFunction(W, "clock_res_get", (int id, int @out) => E(() => U64(@out, 1000)));
        l.DefineFunction(W, "clock_time_get", (int clock, long precision, int @out) => E(() => ClockTimeGet(clock, @out)));
        l.DefineFunction(W, "fd_advise", (int fd, long offset, long length, int advice) => E(() => G.Fd(fd)));
        l.DefineFunction(W, "fd_close", (int fd) => E(() => FdClose(fd)));
        l.DefineFunction(W, "fd_datasync", (int fd) => E(() => FdSync(fd)));
        l.DefineFunction(W, "fd_fdstat_get", (int fd, int buf) => E(() => FdFdstatGet(fd, buf)));
        l.DefineFunction(W, "fd_fdstat_set_flags", (int fd, int flags) => E(() => FdFdstatSetFlags(fd, flags)));
        l.DefineFunction(W, "fd_filestat_get", (int fd, int buf) => E(() => FdFilestatGet(fd, buf)));
        l.DefineFunction(W, "fd_filestat_set_size", (int fd, long size) => E(() => FdFilestatSetSize(fd, size)));
        l.DefineFunction(W, "fd_filestat_set_times", (int fd, long atim, long mtim, int flags) => SUCCESS);
        l.DefineFunction(W, "fd_pread", (int fd, int iovs, int n, long offset, int nread) =>
            E(() => { if (offset < 0) throw new WasiException(EINVAL); U32(nread, ReadInto(fd, iovs, n, offset)); }));
        l.DefineFunction(W, "fd_prestat_get", (int fd, int buf) => E(() => FdPrestatGet(fd, buf)));
        l.DefineFunction(W, "fd_prestat_dir_name", (int fd, int buf, int n) => E(() => FdPrestatDirName(fd, buf, n)));
        l.DefineFunction(W, "fd_pwrite", (int fd, int iovs, int n, long offset, int nwritten) =>
            E(() => { if (offset < 0) throw new WasiException(EINVAL); U32(nwritten, WriteFrom(fd, iovs, n, offset)); }));
        l.DefineFunction(W, "fd_read", (int fd, int iovs, int n, int nread) => E(() => U32(nread, ReadInto(fd, iovs, n, null))));
        l.DefineFunction(W, "fd_readdir", (int fd, int buf, int n, long cookie, int used) => E(() => FdReaddir(fd, buf, n, cookie, used)));
        l.DefineFunction(W, "fd_seek", (int fd, long offset, int whence, int @out) => E(() => FdSeek(fd, offset, whence, @out)));
        l.DefineFunction(W, "fd_sync", (int fd) => E(() => FdSync(fd)));
        l.DefineFunction(W, "fd_tell", (int fd, int @out) => E(() => FdTell(fd, @out)));
        l.DefineFunction(W, "fd_write", (int fd, int iovs, int n, int nwritten) => E(() => U32(nwritten, WriteFrom(fd, iovs, n, null))));
        l.DefineFunction(W, "path_create_directory", (int fd, int ptr, int len) => E(() => PathCreateDirectory(fd, ptr, len)));
        l.DefineFunction(W, "path_filestat_get", (int fd, int flags, int ptr, int len, int buf) => E(() => PathFilestatGet(fd, flags, ptr, len, buf)));
        l.DefineFunction(W, "path_filestat_set_times", (int fd, int flags, int ptr, int len, long atim, long mtim, int fst) =>
            E(() => PathFilestatSetTimes(fd, flags, ptr, len, mtim, fst)));
        l.DefineFunction(W, "path_link", (int fd1, int flags, int oldPtr, int oldLen, int fd2, int newPtr, int newLen) =>
            E(() => PathLink(fd1, flags, oldPtr, oldLen, fd2, newPtr, newLen)));
        l.DefineFunction(W, "path_open", (int fd, int dirflags, int ptr, int len, int oflags, long rights, long inheriting, int fdflags, int @out) =>
            E(() => PathOpen(fd, ptr, len, oflags, rights, fdflags, @out)));
        l.DefineFunction(W, "path_readlink", (int fd, int ptr, int len, int buf, int n, int used) => E(() => PathReadlink(fd, ptr, len, buf, n, used)));
        l.DefineFunction(W, "path_remove_directory", (int fd, int ptr, int len) => E(() => PathRemoveDirectory(fd, ptr, len)));
        l.DefineFunction(W, "path_rename", (int fd1, int oldPtr, int oldLen, int fd2, int newPtr, int newLen) =>
            E(() => PathRename(fd1, oldPtr, oldLen, fd2, newPtr, newLen)));
        l.DefineFunction(W, "path_symlink", (int oldPtr, int oldLen, int fd, int newPtr, int newLen) => E(() => PathSymlink(oldPtr, oldLen, fd, newPtr, newLen)));
        l.DefineFunction(W, "path_unlink_file", (int fd, int ptr, int len) => E(() => PathUnlinkFile(fd, ptr, len)));
        l.DefineFunction(W, "poll_oneoff", (int subs, int events, int n, int nevents) => E(() => PollOneoff(subs, events, n, nevents)));
        l.DefineFunction(W, "proc_exit", (int code) => throw new ProcExit(code));
        l.DefineFunction(W, "sched_yield", () => SUCCESS);
        l.DefineFunction(W, "random_get", (int buf, int n) => E(() => RandomGet(buf, n)));
        l.DefineFunction(W, "sock_accept", (int fd, int flags, int @out) => ENOTSUP);
        l.DefineFunction(W, "sock_recv", (int fd, int iovs, int n, int flags, int nread, int oflags) => ENOTSUP);
        l.DefineFunction(W, "sock_send", (int fd, int iovs, int n, int flags, int nwritten) => ENOTSUP);
        l.DefineFunction(W, "sock_shutdown", (int fd, int how) => ENOTSUP);
        l.DefineFunction("bashkit", "http_request", (int ptr, int n, long timeoutMs, int outLen) => HttpRequest(ptr, n, timeoutMs, outLen));
        l.DefineFunction("bashkit", "http_take", (int buf, int n) => HttpTake(buf, n));
    }

    // ---------- http bridge ----------

    // bashkit.http_request(req_ptr, req_len, timeout_ms, out_len_ptr) -> status; bashkit.http_take(buf, len) -> status.
    // The request is length-prefixed little-endian: method, url, u32 header count, (name, value)..., body (each a u32
    // length then the bytes). A success payload is u32 status, u32 header count, (name, value)..., body; a failure's is the
    // error text. http_request stores the payload and writes its size; http_take copies it into a buffer of exactly that size.
    internal const int HTTP_OK = 0, HTTP_NETWORK = 1, HTTP_TIMEOUT = 2, HTTP_INVALID = 3, HTTP_FAULT = 4;
    static readonly string[] HttpMethods = ["GET", "POST", "PUT", "DELETE", "HEAD", "PATCH"];
    const int MaxUrl = 8192;
    const int MaxHeaders = 128;
    const int MaxHeaderBytes = 64 * 1024;
    const int MaxRequestBody = 16 << 20;
    const int MaxErrorText = 512;

    /// <summary>(method, url, headers, body) from a guest request record. Throws FormatException with the reason.</summary>
    internal static (string Method, string Url, List<KeyValuePair<string, string>> Headers, byte[] Body) DecodeRequest(byte[] buf)
    {
        int at = 0;
        var utf8 = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true);

        uint Count()
        {
            if (at + 4 > buf.Length) throw new FormatException("truncated request");
            var n = BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(at));
            at += 4;
            return n;
        }

        byte[] Take(int limit, string what)
        {
            var n = Count();
            if (n > limit) throw new FormatException($"{what} too long");
            if (at + n > buf.Length) throw new FormatException("truncated request");
            at += (int)n;
            return buf[(at - (int)n)..at];
        }

        string Str(int limit, string what)
        {
            try { return utf8.GetString(Take(limit, what)); }
            catch (System.Text.DecoderFallbackException) { throw new FormatException($"{what} is not UTF-8"); }
        }

        var method = Str(16, "method").ToUpperInvariant();
        if (!HttpMethods.Contains(method)) throw new FormatException($"unsupported HTTP method: {method}");
        var url = Str(MaxUrl, "URL");
        if (url.Any(ch => ch <= ' ' || ch == '\x7f')) throw new FormatException("URL contains control characters or spaces");
        var count = Count();
        if (count > MaxHeaders) throw new FormatException("too many headers");
        var headers = new List<KeyValuePair<string, string>>();
        int size = 0;
        for (int i = 0; i < count; i++)
        {
            string name = Str(MaxHeaderBytes, "header name"), value = Str(MaxHeaderBytes, "header value");
            size += name.Length + value.Length;
            if (size > MaxHeaderBytes) throw new FormatException("headers too large");
            if (name.Length == 0 || name.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || "!#$%&'*+-.^_`|~".Contains(ch)))) throw new FormatException("invalid header name");
            if (value.Any(ch => ch is '\r' or '\n' or '\0')) throw new FormatException("invalid header value");
            headers.Add(new(name, value.Trim()));
        }
        var body = Take(MaxRequestBody, "request body");
        if (at != buf.Length) throw new FormatException("trailing bytes in request");
        return (method, url, headers, body);
    }

    internal static byte[] EncodeResponse(int status, IEnumerable<KeyValuePair<string, string>> headers, byte[] body)
    {
        var output = new MemoryStream();
        void U(long v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, (uint)v); output.Write(b); }
        var list = headers.ToList();
        U(status);
        U(list.Count);
        foreach (var (k, v) in list)
        {
            var kb = System.Text.Encoding.UTF8.GetBytes(k);
            var vb = System.Text.Encoding.UTF8.GetBytes(v);
            U(kb.Length); output.Write(kb);
            U(vb.Length); output.Write(vb);
        }
        U(body.Length);
        output.Write(body);
        return output.ToArray();
    }

    internal static byte[] ErrorPayload(string text)
    {
        var b = System.Text.Encoding.UTF8.GetBytes(text);
        return b.Length <= MaxErrorText ? b : b[..MaxErrorText];
    }

    static int HttpRequest(int ptr, int n, long timeoutMs, int outLen)
    {
        var g = G;
        byte[] raw;
        try { raw = Read(ptr, n); }
        catch (WasiException) { return HTTP_FAULT; }
        g.Pending = null;
        int code;
        byte[] payload;
        try
        {
            var (method, url, headers, body) = DecodeRequest(raw);
            var left = g.Deadline - DateTime.UtcNow;
            if (left < TimeSpan.Zero) left = TimeSpan.Zero;
            var timeout = timeoutMs < 0 ? left : TimeSpan.FromMilliseconds(Math.Min(left.TotalMilliseconds, timeoutMs));
            (code, payload) = g.Http(method, url, headers, body, timeout); // the web policy counts and limits requests
            if (code != HTTP_OK) payload = ErrorPayload(System.Text.Encoding.UTF8.GetString(payload));
        }
        catch (FormatException e) { (code, payload) = (HTTP_INVALID, ErrorPayload(e.Message)); }
        try { U32(outLen, payload.Length); }
        catch (WasiException) { return HTTP_FAULT; }
        g.Pending = payload;
        return code;
    }

    static int HttpTake(int buf, int n)
    {
        var g = G;
        var payload = g.Pending;
        g.Pending = null;
        if (payload is null || n != payload.Length) return HTTP_FAULT;
        try { Write(buf, payload); }
        catch (WasiException) { return HTTP_FAULT; }
        return HTTP_OK;
    }

    // ---------- running a call ----------

    /// <summary>How a call ended: Code (the exit status) unless TimedOut, StackOverflow, or Trap (another fatal error's text).</summary>
    internal sealed record Outcome(int Code = 1, bool TimedOut = false, bool StackOverflow = false, string? Trap = null);

    /// <summary>Runs bashkit_run on a fresh instance, on this thread; saves the files the program left open. Throws OutputFull.</summary>
    internal static Outcome Run(Runtime rt, Guest guest)
    {
        using var store = new Store(rt.Engine);
        store.SetLimits(memorySize: guest.MaxMemory, instances: 1, tables: 4, memories: 1);
        // the deadline is checked every tick, not counted in ticks: `timeout` cancels the call's token at a time we don't know
        store.SetEpochDeadline(1);
        store.SetEpochDeadlineCallback(_ => guest.TimedOut ? throw new Interrupted() : 1UL);
        t_guest = guest;
        try
        {
            using (rt.Ticking())
            {
                var instance = rt.Linker.Instantiate(store, rt.Module);
                guest.Memory = instance.GetMemory("memory");
                instance.GetAction("_initialize")?.Invoke();
                var run = instance.GetFunction<int>("bashkit_run") ?? throw new WasmtimeException("the module has no bashkit_run");
                return new Outcome(run());
            }
        }
        catch (Exception e) when (e is WasmtimeException or ProcExit or Interrupted)
        {
            for (Exception? x = e; x != null; x = x.InnerException)
                switch (x)
                {
                    case ProcExit p: return new Outcome(p.Code);
                    case Interrupted: return new Outcome(TimedOut: true);
                    case OutputFull o: throw o;
                }
            if (e is TrapException { Type: TrapCode.Interrupt } || guest.TimedOut) return new Outcome(TimedOut: true);
            if (e is TrapException { Type: TrapCode.StackOverflow }) return new Outcome(StackOverflow: true);
            var line = e.Message.Split('\n')[0];
            return new Outcome(Trap: line.Length > 200 ? line[..200] : line);
        }
        finally
        {
            t_guest = null;
            guest.FlushAll();
            guest.Memory = null;
        }
    }
}
