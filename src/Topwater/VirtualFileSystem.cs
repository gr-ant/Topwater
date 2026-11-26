namespace Topwater;

/// <summary>Thrown by commands and the filesystem; becomes "cmd: message" on stderr with the given exit code.</summary>
public class ShellException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>A bad command line: printed with GNU's "Try 'cmd --help'" hint; exits 1, or 2 for ls, grep, sort and diff.</summary>
public sealed class UsageException(string message) : ShellException(message, 1);

/// <param name="Mode">Unix permission bits, e.g. 0x1ED (0755).</param>
/// <param name="Links">Hard links: 1 for a file (more after `ln`); 2 plus one per subdirectory for a directory, as on ext4.</param>
/// <param name="LinkTarget">For a symbolic link (a stat that didn't follow it): the path it holds. Its Size is that path's length in bytes.</param>
public readonly record struct FileStat(bool IsDirectory, long Size, DateTime Modified, int Mode, string Owner, int Links = 1, string? LinkTarget = null)
{
    public bool IsSymlink => LinkTarget != null;

    /// <summary>512-byte blocks in use, as `stat %b` and `ls -s` count them: whole 4 KiB filesystem blocks.</summary>
    public long Blocks => IsSymlink ? 0 : (Size + 4095) / 4096 * 8; // a short link lives in its inode: no blocks
}

/// <summary>
/// In-memory Unix-style filesystem with owners and permission bits. All paths are absolute or resolved against "/".
/// Calls from your app are unrestricted (root). While the shell runs the agent's commands it sets <see cref="RunAs"/>,
/// so the agent is an ordinary user: it can't write to root-owned places such as /etc or /usr/bin.
/// Apps can "mount" data for the agent by writing files, e.g. <c>fs.WriteAllText("/data/orders.json", json)</c>;
/// new files belong to the owner of the directory they're created in.
/// Binary files (<see cref="WriteAllBytes"/>, <see cref="AddDocument(string, byte[], bool)"/>) keep their exact bytes; the text API reads them as lossy UTF-8.
/// shortcut: no groups, no locking; add them if group permissions or concurrent shells are needed.
/// </summary>
public sealed class VirtualFileSystem
{
    abstract class Node
    {
        DateTime _modified = DateTime.UtcNow;
        string _owner = "root";
        int _mode, _nlink = 1;
        public DateTime Modified { get => _modified; set { _modified = value; Touch(); } }
        public string Owner { get => _owner; set { _owner = value; Touch(); } }
        public int Mode { get => _mode; set { _mode = value; Touch(); } }
        public int Nlink { get => _nlink; set { _nlink = value; Touch(); } } // names this file or link has (hard links); unused for directories
        /// <summary>When this node (or a directory's list of names) last changed, on <see cref="ChangeClock"/>.</summary>
        public long Changed = Interlocked.Increment(ref ChangeClock);
        public void Touch() => Changed = Interlocked.Increment(ref ChangeClock);
    }
    /// <summary>Counts changes to every filesystem's nodes, so a <see cref="ChangeTracker"/> visits only what moved since it last looked.</summary>
    static long ChangeClock;
    sealed class Dir : Node
    {
        public readonly Names Children;
        public long Listed = Interlocked.Increment(ref ChangeClock); // when a name was last added, removed or replaced
        public Dir() => Children = new(this);
    }
    /// <summary>A directory's names; adding, removing or replacing one marks the directory's list changed.</summary>
    sealed class Names(Dir owner) : SortedDictionary<string, Node>(StringComparer.Ordinal)
    {
        public new Node this[string name] { get => base[name]; set { base[name] = value; Mark(); } }
        public new void Add(string name, Node node) { base.Add(name, node); Mark(); }
        public bool TryAdd(string name, Node node) { if (ContainsKey(name)) return false; Add(name, node); return true; }
        public new bool Remove(string name) { if (!base.Remove(name)) return false; Mark(); return true; }
        void Mark() => owner.Listed = Interlocked.Increment(ref ChangeClock);
    }
    sealed class LinkNode : Node { string _target = ""; public string Target { get => _target; set { _target = value; Touch(); } } }
    sealed class FileNode : Node
    {
        string? _text = "";
        System.Text.StringBuilder? _appended; // `>>` writes accumulate here, so append loops stay linear
        byte[]? _data;
        long? _reportedSize;
        long _size;
        public string Content
        {
            // the builder is dropped once read, so a file isn't held twice; the next append copies it once (as the read did)
            get { if (_text == null) { _text = _appended!.ToString(); _appended = null; } return _text; }
            set { _text = value; _appended = null; Touch(); }
        }
        public byte[]? Data { get => _data; set { _data = value; Touch(); } } // set for binary files; Content is then unused
        public long? ReportedSize { get => _reportedSize; set { _reportedSize = value; Touch(); } } // what `ls -l` and `stat` show for a program stub, whose real content is a few bytes
        public long Size { get => _size; set { _size = value; Touch(); } } // real bytes held, as counted against the filesystem's capacity
        public byte[] Raw => Data ?? System.Text.Encoding.UTF8.GetBytes(Content);

        /// <param name="reserve">Called with the change in size before anything changes; throws to refuse the write.</param>
        public void Store(string? text, byte[]? bytes, bool append, Action<long> reserve)
        {
            if (bytes == null && ByteText.HasEscapes(text)) bytes = ByteText.Encode(text!); // binary that came through a pipe
            if (bytes != null || (append && Data != null))
            {
                bytes ??= System.Text.Encoding.UTF8.GetBytes(text!);
                long size = append ? Size + bytes.Length : bytes.Length;
                reserve(size - Size);
                Data = append ? [.. Raw, .. bytes] : bytes;
                Content = "";
                Size = size;
            }
            else
            {
                long size = (append ? Size : 0) + System.Text.Encoding.UTF8.GetByteCount(text!);
                reserve(size - Size);
                Size = size;
                if (append)
                {
                    _appended ??= new System.Text.StringBuilder(Content);
                    _appended.Append(text);
                    _text = null;
                }
                else Content = text!;
                Data = null;
            }
        }
    }

    public const int DefaultFileMode = 0b110_100_100; // 0644
    public const int DefaultDirMode = 0b111_101_101;  // 0755

    /// <summary>The shell's umask: bits taken away from what the agent's commands create (your own calls create 0644 and 0755).</summary>
    internal int Umask { get; set; } = 0b000_010_010; // 0022
    int Masked(int mode) => mode & ~(RunAs == null ? 0b000_010_010 : Umask);

    Dir _root = new() { Mode = DefaultDirMode };

    /// <summary>Where new modification times come from: the owning shell's clock (virtual when ShellOptions.StartTime is set).</summary>
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    /// <summary>Most bytes the files may hold in total; the agent's writes past it fail with "No space left on device".
    /// Writes from your app are counted but never refused. <see cref="Shell"/> sets it from <see cref="ShellOptions.MaxFileSystemBytes"/>.</summary>
    public long Capacity { get; set; } = long.MaxValue;

    /// <summary>Bytes all files hold now (what `df` reports as used).</summary>
    public long UsedBytes { get; private set; }

    /// <summary>Counts a change in stored bytes, refusing growth past <see cref="Capacity"/> for the agent (RunAs set).</summary>
    void Reserve(long delta, string path)
    {
        if (delta > 0 && RunAs != null && UsedBytes + delta > Capacity) throw new ShellException($"{path}: {NoSpace}");
        UsedBytes += delta;
    }

    internal const string NoSpace = CommandSpec.Errno.ENOSPC;

    static long TreeSize(Node n) => n switch { FileNode f => f.Size, Dir d => d.Children.Values.Sum(TreeSize), _ => 0 };

    /// <summary>Drops one name of each node in a tree being removed; returns the bytes freed (a file goes with its last name).</summary>
    static long Unlink(Node n) => n switch
    {
        Dir d => d.Children.Values.Sum(Unlink),
        _ when --n.Nlink > 0 => 0,
        FileNode f => f.Size,
        _ => 0,
    };

    // async-local, so the app's own calls stay unrestricted while an agent command is awaiting (and concurrent shells sharing
    // this filesystem don't see each other's user); commands the app registers run inside the agent's flow, as the agent
    readonly AsyncLocal<string?> _runAs = new();

    /// <summary>User the agent's commands run as, or null for unrestricted access (your app). Flows with the async
    /// call that sets it: <see cref="Shell.RunAsync"/> and the commands it runs see the agent; your other code doesn't.</summary>
    public string? RunAs { get => _runAs.Value; set => _runAs.Value = value; }

    bool Restricted => RunAs is not null and not "root";
    string? User => RunAs is "root" ? null : RunAs; // null: unrestricted

    /// <summary>Temporarily lifts <see cref="RunAs"/> restrictions (for shell internals).</summary>
    public IDisposable AsRoot()
    {
        var saved = RunAs;
        RunAs = null;
        return new Restore(() => RunAs = saved);
    }

    sealed class Restore(Action a) : IDisposable { public void Dispose() => a(); }

    /// <summary>Resolves <paramref name="path"/> against <paramref name="cwd"/> and collapses "." and "..".</summary>
    public static string Normalize(string cwd, string path)
    {
        var full = path.StartsWith('/') ? path : cwd.TrimEnd('/') + "/" + path;
        var stack = new List<string>();
        foreach (var seg in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == "..") { if (stack.Count > 0) stack.RemoveAt(stack.Count - 1); continue; }
            stack.Add(seg);
        }
        return "/" + string.Join('/', stack);
    }

    public static string GetName(string path) => Normalize("/", path) is var p && p == "/" ? "/" : p[(p.LastIndexOf('/') + 1)..];
    public static string GetParent(string path) => Normalize("/", path) is var p && p.LastIndexOf('/') is var i and > 0 ? p[..i] : "/";

    /// <summary>"drwxr-xr-x" style rendering of a mode.</summary>
    public static string FormatMode(bool isDirectory, int mode)
    {
        var chars = new char[10];
        chars[0] = isDirectory ? 'd' : '-';
        const string rwx = "rwxrwxrwx";
        for (int i = 0; i < 9; i++) chars[i + 1] = (mode & (1 << (8 - i))) != 0 ? rwx[i] : '-';
        if ((mode & 0x800) != 0) chars[3] = chars[3] == 'x' ? 's' : 'S'; // setuid (04000)
        if ((mode & 0x400) != 0) chars[6] = chars[6] == 'x' ? 's' : 'S'; // setgid (02000)
        if ((mode & 0x200) != 0) chars[9] = chars[9] == 'x' ? 't' : 'T'; // sticky bit (01000)
        return new string(chars);
    }

    /// <summary>"lrwxrwxrwx" for a symbolic link, else as <see cref="FormatMode(bool, int)"/>.</summary>
    public static string FormatMode(FileStat st) => st.IsSymlink ? "l" + FormatMode(false, st.Mode)[1..] : FormatMode(st.IsDirectory, st.Mode);

    /// <summary>Symbolic links followed in one lookup before it fails with <see cref="Loop"/>, as on Linux.</summary>
    const int MaxLinkHops = 40;
    internal const string Loop = CommandSpec.Errno.ELOOP;

    /// <summary>Where a path leads: the directory holding (or that would hold) its last entry, that entry's name and node
    /// (null if absent), and its physical path. Parent is null when the path can't be reached (Why says why), and for "/".</summary>
    readonly record struct Spot(Dir? Parent, string Name, Node? Node, string Real, string Why);

    /// <summary>Walks <paramref name="path"/>; like Linux, each directory passed through needs search (x) permission, symbolic
    /// links in the middle are followed (a relative one from the directory holding it), and the last one only when
    /// <paramref name="follow"/>. ".." goes up from where the walk really is. A loop fails after 40 links, never hangs.</summary>
    Spot Walk(string path, bool follow = true)
    {
        var user = User;
        var chain = new List<(string Name, Dir Dir)> { ("", _root) }; // the directories walked into, for ".." and the real path
        var todo = new Stack<string>();
        void Push(string p)
        {
            var segs = p.Split('/');
            for (int i = segs.Length - 1; i >= 0; i--) todo.Push(segs[i]);
        }
        Push(path);
        string Real(string name) => string.Concat(chain.Skip(1).Select(c => "/" + c.Name)) + "/" + name;
        static Spot Fail(string why) => new(null, "", null, "", why);
        int hops = 0;
        bool onFile = false; // a file was reached with more of the path to go
        while (todo.Count > 0)
        {
            var seg = todo.Pop();
            if (onFile) return Fail(CommandSpec.Errno.ENOTDIR);
            if (seg is "" or ".") continue;
            var dir = chain[^1].Dir;
            if (!Permits(dir, user, 1)) return Fail(CommandSpec.Errno.EACCES);
            if (seg == "..") { if (chain.Count > 1) chain.RemoveAt(chain.Count - 1); continue; }
            bool final = todo.All(t => t is "" or "."), bare = todo.Count == 0; // bare: no trailing slash, which would mean a directory
            if (!dir.Children.TryGetValue(seg, out var next))
                return final ? new(bare ? dir : null, seg, null, Real(seg), CommandSpec.Errno.ENOENT) : Fail(CommandSpec.Errno.ENOENT);
            if (next is LinkNode link && (!bare || follow))
            {
                if (++hops > MaxLinkHops) return Fail(Loop);
                if (link.Target.StartsWith('/')) chain.RemoveRange(1, chain.Count - 1);
                Push(link.Target);
                continue;
            }
            if (bare) return new(dir, seg, next, Real(seg), "");
            if (next is Dir d) chain.Add((seg, d)); else onFile = true;
        }
        return chain.Count == 1 ? new(null, "/", _root, "/", "") : new(chain[^2].Dir, chain[^1].Name, chain[^1].Dir, Real("")[..^1], "");
    }

    Node? Find(string path, bool follow = true) => Walk(path, follow).Node;

    Node? Find(string path, out string why, bool follow = true)
    {
        var spot = Walk(path, follow);
        why = spot.Why is "" ? CommandSpec.Errno.ENOENT : spot.Why;
        return spot.Node;
    }

    /// <summary>Why <paramref name="path"/> can't be reached: "Permission denied", "Not a directory", "Too many levels of
    /// symbolic links" or "No such file or directory".</summary>
    internal string WhyMissing(string path, bool follow = true) => Find(path, out var why, follow) == null ? why : CommandSpec.Errno.ENOENT;

    /// <summary>Where an entry is created, removed or renamed: its directory (which needs search permission) and name. The last
    /// name is not followed, so this is the link itself when it is one.</summary>
    Spot Place(string path)
    {
        var spot = Walk(path, follow: false);
        if (spot.Parent == null) throw new ShellException($"{path}: {(spot.Node != null ? "Device or resource busy" : spot.Why)}");
        return spot;
    }

    Dir ParentDir(string path) => Place(path).Parent!;

    static bool Permits(Node n, string? user, int bits) => user == null || ((n.Mode >> (n.Owner == user ? 6 : 0)) & bits) == bits;
    bool Allowed(Node n, int bit) => Permits(n, User, bit);

    void Require(Node n, int bit, string path)
    {
        if (!Allowed(n, bit)) throw new ShellException($"{path}: {CommandSpec.Errno.EACCES}");
    }

    /// <summary>In a sticky directory (/tmp) only the entry's owner or the directory's may remove or rename the entry.</summary>
    void RequireNotSticky(Dir dir, Node entry, string path)
    {
        if (User is { } u && (dir.Mode & 0x200) != 0 && entry.Owner != u && dir.Owner != u)
            throw new ShellException($"{path}: {CommandSpec.Errno.EPERM}");
    }

    /// <param name="follow">false: a symbolic link counts itself (lstat), even one whose target is missing.</param>
    public bool Exists(string path, bool follow = true) => Find(path, follow) != null;
    public bool IsDirectory(string path, bool follow = true) => Find(path, follow) is Dir;
    public bool IsFile(string path, bool follow = true) => Find(path, follow) is FileNode;
    public bool IsSymlink(string path) => Find(path, follow: false) is LinkNode;
    public bool CanRead(string path) => Find(path) is { } n && Allowed(n, 4);
    public bool CanWrite(string path) => Find(path) is { } n && Allowed(n, 2);
    public bool CanExecute(string path) => Find(path) is { } n && Allowed(n, 1);

    /// <summary>Whether two paths lead to the same file or directory (`test -ef`): through hard or symbolic links.</summary>
    public bool SameFile(string a, string b, bool follow = true) => Find(a, follow) is { } n && ReferenceEquals(n, Find(b, follow));

    /// <param name="follow">false: a symbolic link's own status (lstat), with <see cref="FileStat.LinkTarget"/> set.</param>
    public FileStat Stat(string path, bool follow = true) => Find(path, out var why, follow) switch
    {
        Dir d => new(true, 4096, d.Modified, d.Mode, d.Owner, 2 + d.Children.Values.Count(c => c is Dir)),
        FileNode f => new(false, f.ReportedSize ?? f.Data?.Length ?? System.Text.Encoding.UTF8.GetByteCount(f.Content), f.Modified, f.Mode, f.Owner, f.Nlink),
        LinkNode l => new(false, System.Text.Encoding.UTF8.GetByteCount(l.Target), l.Modified, l.Mode, l.Owner, l.Nlink, l.Target),
        _ => throw new ShellException($"{path}: {why}"),
    };

    /// <summary>The path a symbolic link holds (`readlink`); "Invalid argument" for anything else.</summary>
    public string ReadLink(string path) => Find(path, out var why, follow: false) switch
    {
        LinkNode l => l.Target,
        null => throw new ShellException($"{path}: {why}"),
        _ => throw new ShellException($"{path}: {CommandSpec.Errno.EINVAL}"),
    };

    /// <summary>The physical absolute path with every symbolic link resolved (`realpath`, `pwd -P`). With
    /// <paramref name="existing"/> false the last name may be missing (`readlink -f`). Null, with <paramref name="why"/>,
    /// when it can't be resolved.</summary>
    public string? RealPath(string path, out string why, bool existing = true)
    {
        var spot = Walk(path);
        why = spot.Why is "" ? CommandSpec.Errno.ENOENT : spot.Why;
        return spot.Node != null || (!existing && spot.Parent != null) ? spot.Real : null;
    }

    /// <summary>Like <see cref="Normalize"/>, but ".." leaves where the path so far really is, after its symbolic links, as
    /// the kernel resolves it (the shell's cwd itself stays logical, as bash keeps it).</summary>
    public string NormalizePhysical(string cwd, string path)
    {
        var full = path.StartsWith('/') ? path : cwd.TrimEnd('/') + "/" + path;
        // "." or ".." last names a directory itself, never a link to it (a logical cwd may be reached through one)
        if (path.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault(".") is "." or "..")
            return RealPath(full, out _) ?? Normalize("/", full);
        if (!full.Split('/').Contains("..")) return Normalize("/", full);
        var p = "/";
        foreach (var seg in full.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            p = seg == ".." ? GetParent(RealPath(p, out _) ?? p) : p == "/" ? "/" + seg : p + "/" + seg;
        }
        return p;
    }

    /// <summary>Creates a symbolic link at <paramref name="path"/> holding <paramref name="target"/> (`ln -s`), which need not exist.</summary>
    public void CreateSymlink(string path, string target)
    {
        if (target.Length == 0) throw new ShellException($"{path}: {CommandSpec.Errno.ENOENT}");
        var spot = Place(path);
        if (spot.Node != null) throw new ShellException($"{path}: {CommandSpec.Errno.EEXIST}");
        Require(spot.Parent!, 2, path);
        spot.Parent!.Children[spot.Name] = new LinkNode { Target = target, Owner = RunAs ?? spot.Parent.Owner, Mode = 0x1FF, Modified = Clock() };
        spot.Parent.Modified = Clock();
    }

    /// <summary>Gives <paramref name="existing"/> a second name, <paramref name="path"/> (`ln`). With <paramref name="follow"/>
    /// false a symbolic link is linked itself. Like Linux with protected_hardlinks, the agent may only link files it owns or
    /// can read and write.</summary>
    public void CreateHardLink(string existing, string path, bool follow = false)
    {
        var node = Find(existing, out var why, follow) ?? throw new ShellException($"{existing}: {why}");
        if (node is Dir) throw new ShellException($"{existing}: hard link not allowed for directory");
        var spot = Place(path);
        if (spot.Node != null) throw new ShellException($"{path}: {CommandSpec.Errno.EEXIST}");
        Require(spot.Parent!, 2, path);
        if (Restricted && node.Owner != RunAs && !Allowed(node, 6)) throw new ShellException($"{path}: {CommandSpec.Errno.EPERM}");
        node.Nlink++;
        spot.Parent!.Children[spot.Name] = node;
        spot.Parent.Modified = Clock();
    }

    public string ReadAllText(string path) => ReadNode(path) is var f && f.Data == null ? f.Content : System.Text.Encoding.UTF8.GetString(f.Data);

    /// <summary>What `cat` and `&lt;` put on a pipe: like <see cref="ReadAllText"/>, but a binary file's bytes survive (see <see cref="ByteText"/>).</summary>
    internal string ReadPipeText(string path) => ReadNode(path) is var f && f.Data == null ? f.Content : ByteText.Decode(f.Data);

    /// <summary>The file's exact bytes (UTF-8 for files written as text).</summary>
    public byte[] ReadAllBytes(string path) => ReadNode(path) is var f && f.Data != null ? (byte[])f.Data.Clone() : f.Raw;

    FileNode ReadNode(string path)
    {
        switch (Find(path, out var why))
        {
            case FileNode f: Require(f, 4, path); return f;
            case Dir: throw new ShellException($"{path}: {CommandSpec.Errno.EISDIR}");
            default: throw new ShellException($"{path}: {why}");
        }
    }

    public void WriteAllText(string path, string content) => Write(path, content, null, append: false);
    public void AppendAllText(string path, string content) => Write(path, content, null, append: true);
    public void WriteAllBytes(string path, byte[] data) => Write(path, null, (byte[])data.Clone(), append: false);

    /// <summary>Mounts a real document from the host for the agent; see <see cref="AddDocument(string, byte[], bool)"/>.</summary>
    public void AddDocument(string vfsPath, string hostPath, bool alsoMarkdown = false) => AddDocument(vfsPath, File.ReadAllBytes(hostPath), alsoMarkdown);

    /// <summary>Stores a document's bytes at <paramref name="vfsPath"/> (creating parent directories); with
    /// <paramref name="alsoMarkdown"/>, also writes its Markdown conversion to "&lt;vfsPath&gt;.md".</summary>
    public void AddDocument(string vfsPath, byte[] data, bool alsoMarkdown = false)
    {
        CreateDirectory(GetParent(vfsPath), parents: true);
        WriteAllBytes(vfsPath, data);
        if (alsoMarkdown) WriteAllText(vfsPath + ".md", MarkdownConverter.ToMarkdown(data, GetName(vfsPath)));
    }

    void Write(string path, string? content, byte[]? data, bool append)
    {
        path = Normalize("/", path);
        if (path == "/dev/null" && Find(path) != null) return;
        var spot = Walk(path); // through a symbolic link, even a dangling one: its target is written or created
        var dir = spot.Parent ?? throw new ShellException($"{path}: {(spot.Node != null ? CommandSpec.Errno.EISDIR : spot.Why)}");
        if (spot.Node is { } existing)
        {
            if (existing is not FileNode f) throw new ShellException($"{path}: {CommandSpec.Errno.EISDIR}");
            Require(f, 2, path);
            f.Store(content, data, append, d => Reserve(d, path));
            f.Modified = Clock();
        }
        else
        {
            Require(dir, 2, path);
            var file = new FileNode { Owner = RunAs ?? dir.Owner, Mode = Masked(0b110_110_110), Modified = Clock() };
            file.Store(content, data, false, d => Reserve(d, path));
            dir.Children[spot.Name] = file;
        }
        dir.Modified = Clock();
    }

    /// <summary>Back-dates a path (and optionally everything under it), so system files don't all look brand new.</summary>
    internal void SetModified(string path, DateTime when, bool recursive = false)
    {
        if (Find(path) is not { } node) return;
        void Walk(Node n)
        {
            n.Modified = when;
            if (recursive && n is Dir d) foreach (var child in d.Children.Values) Walk(child);
        }
        Walk(node);
    }

    /// <summary>Sets a modification time (tar and unzip restoring one); as with utime(2), only the owner may.</summary>
    internal void SetModifiedTime(string path, DateTime when, bool follow = true)
    {
        var node = Find(path, out var why, follow) ?? throw new ShellException($"{path}: {why}");
        if (Restricted && node.Owner != RunAs) throw new ShellException($"{path}: {CommandSpec.Errno.EPERM}");
        node.Modified = when;
    }

    internal void SetReportedSize(string path, long size)
    {
        if (Find(path) is FileNode f) f.ReportedSize = size;
    }

    public void Touch(string path)
    {
        if (Find(path, out var why) is { } n)
        {
            Require(n, 2, path);
            n.Modified = Clock();
        }
        else if (why == CommandSpec.Errno.EACCES) throw new ShellException($"{path}: {CommandSpec.Errno.EACCES}");
        else WriteAllText(path, "");
    }

    public void CreateDirectory(string path, bool parents = false)
    {
        path = Normalize("/", path);
        if (Find(path, follow: false) != null) // a symbolic link counts, even a dangling one
        {
            if (parents && Find(path) is Dir) return;
            throw new ShellException($"cannot create directory '{path}': {CommandSpec.Errno.EEXIST}");
        }
        if (parents && !Exists(GetParent(path)))
        {
            CreateDirectory(GetParent(path), true);
            if (Find(GetParent(path)) is Dir made) made.Mode |= 0b011_000_000; // mkdir -p: a parent it makes stays writable and searchable by its owner
        }
        var spot = Place(path);
        Require(spot.Parent!, 2, path);
        spot.Parent!.Children[spot.Name] = new Dir { Owner = RunAs ?? spot.Parent.Owner, Mode = Masked(0b111_111_111), Modified = Clock() };
        spot.Parent.Modified = Clock();
    }

    /// <summary>Entry names in a directory, sorted ordinally.</summary>
    public IReadOnlyList<string> List(string path)
    {
        switch (Find(path, out var why))
        {
            case Dir d: Require(d, 4, path); return d.Children.Keys.ToList();
            case null: throw new ShellException($"{path}: {why}");
            default: throw new ShellException($"{path}: {CommandSpec.Errno.ENOTDIR}");
        }
    }

    /// <summary>Removes an entry (a symbolic link itself, never what it leads to); <paramref name="recursive"/> for a
    /// directory with entries, whose links are removed, not followed.</summary>
    public void Delete(string path, bool recursive = false)
    {
        path = Normalize("/", path);
        if (path == "/") throw new ShellException("it is dangerous to operate recursively on '/'");
        var node = Find(path, out var why, follow: false) ?? throw new ShellException($"{path}: {why}");
        if (node is Dir d && d.Children.Count > 0 && !recursive) throw new ShellException($"{path}: {CommandSpec.Errno.ENOTEMPTY}");
        var spot = Place(path);
        var parent = spot.Parent!;
        if (node is Dir tree) RequireTreeRemovable(tree, path);
        Require(parent, 2, path);
        RequireNotSticky(parent, node, path);
        parent.Children.Remove(spot.Name);
        UsedBytes -= Unlink(node);
    }

    // shortcut: all or nothing, naming the first entry that can't go; real rm -r removes what it can.
    void RequireTreeRemovable(Dir d, string path)
    {
        if (d.Children.Count == 0) return;
        var (first, _) = d.Children.First();
        Require(d, 4, path);
        Require(d, 3, path + "/" + first);
        foreach (var (name, child) in d.Children)
        {
            RequireNotSticky(d, child, path + "/" + name);
            if (child is Dir sub) RequireTreeRemovable(sub, path + "/" + name);
        }
    }

    /// <summary>Copies a file or tree. While restricted, anything unreadable fails the whole copy; see the overload.</summary>
    public void Copy(string source, string dest) => Copy(source, dest, (p, _) => throw new ShellException($"{p}: {CommandSpec.Errno.EACCES}"));

    /// <summary>Like `cp -r`: entries the user can't read are skipped and passed to <paramref name="denied"/> (path, is directory).</summary>
    /// <param name="followTop">Copy what <paramref name="source"/> leads to when it is a symbolic link (cp without -r, or -H),
    /// rather than the link.</param>
    /// <param name="followAll">Copy what every symbolic link in the tree leads to (cp -L); a link back into a directory being
    /// copied goes to <paramref name="broken"/> as "cyclic", one leading nowhere with its reason.</param>
    /// <param name="broken">(path, reason) for a link -L can't copy; null to throw.</param>
    /// <param name="tick">Called for each entry copied: the caller's time limit (links can make a copy far bigger than the tree).</param>
    /// <param name="preserve">cp -p: the copies keep their sources' modes and times; otherwise the umask applies and the time is now.</param>
    internal void Copy(string source, string dest, Action<string, bool> denied, bool followTop = true, bool followAll = false, Action<string, string>? broken = null,
        Action? tick = null, bool preserve = false)
    {
        source = Normalize("/", source);
        var node = Find(source, out var why, followTop || followAll) ?? throw new ShellException($"{source}: {why}");
        dest = Normalize("/", dest);
        var place = Place(dest);
        if (node is Dir && (place.Real + "/").StartsWith(RealPath(source, out _) + "/"))
            throw new ShellException($"cannot copy a directory, '{source}', into itself, '{dest}'");
        var target = place.Node is LinkNode ? Find(dest) : place.Node; // a file is copied through a link onto its target
        if (target is Dir && node is not Dir) throw new ShellException($"{dest}: {CommandSpec.Errno.EISDIR}");
        var parent = place.Parent!;
        if (target != null) Require(target, 2, dest); else Require(parent, 2, dest);
        var user = User;
        var copying = new HashSet<Dir>(ReferenceEqualityComparer.Instance); // directories on the way down, so -L can't loop
        long cloned = 0;
        broken ??= (p, reason) => throw new ShellException($"{p}: {reason}");
        Node? Clone(Node n, string path)
        {
            if (n is LinkNode l)
            {
                if (!followAll) return new LinkNode { Target = l.Target, Owner = RunAs ?? parent.Owner, Mode = l.Mode, Modified = Clock() };
                if (Find(path, out var reason) is not { } to) { broken(path, reason); return null; }
                n = to;
            }
            // a directory already being copied further up, reached through a link (even one higher up), as cp -L reports it
            if (n is Dir again && copying.Contains(again)) { broken(path, "cyclic"); return null; }
            tick?.Invoke();
            if (!Permits(n, user, n is Dir ? 5 : 4))
            {
                denied(path, n is Dir);
                return null;
            }
            if (n is FileNode f)
            {
                // the copy is built before it is stored: links can make it far bigger than the tree, so charge it as it grows
                if (RunAs != null && UsedBytes + (cloned += f.Size) > Capacity) throw new ShellException($"{dest}: {NoSpace}");
                return new FileNode { Content = f.Content, Data = f.Data, Size = f.Size, Owner = RunAs ?? parent.Owner, Mode = preserve ? f.Mode : Masked(f.Mode) & ~0xC00, Modified = preserve ? f.Modified : Clock() };
            }
            var dir = (Dir)n;
            var copy = new Dir { Owner = RunAs ?? parent.Owner, Mode = preserve ? n.Mode : Masked(n.Mode), Modified = preserve ? n.Modified : Clock() };
            copying.Add(dir);
            foreach (var (k, v) in dir.Children)
                if (Clone(v, path + "/" + k) is { } c) copy.Children[k] = c;
            copying.Remove(dir);
            return copy;
        }
        if (Clone(node, source) is not { } clone) return;
        // copying onto an existing file writes into it, keeping its owner and mode, as cp does
        if (target is FileNode tf && clone is FileNode cf)
        {
            tf.Store(cf.Content, cf.Data, false, d => Reserve(d, dest));
            tf.Modified = cf.Modified;
            if (preserve) tf.Mode = cf.Mode;
        }
        else
        {
            var old = place.Node;
            long oldSize = old == null ? 0 : TreeSize(old);
            Reserve(TreeSize(clone) - oldSize, dest);
            if (old != null) UsedBytes += oldSize - Unlink(old);
            parent.Children[place.Name] = clone;
        }
    }

    /// <summary>Renames an entry; a symbolic link is moved itself, and a link at <paramref name="dest"/> is replaced, not followed.</summary>
    public void Move(string source, string dest)
    {
        source = Normalize("/", source);
        dest = Normalize("/", dest);
        var node = Find(source, out var why, follow: false) ?? throw new ShellException($"{source}: {why}");
        var from = Place(source);
        var to = Place(dest);
        // by real paths: through a link, a directory could otherwise be moved into itself and cut off from the tree
        if ((to.Real + "/").StartsWith(from.Real + "/")) throw new ShellException($"cannot move '{source}' to a subdirectory of itself, '{dest}'");
        var existing = to.Node;
        if (existing is Dir) throw new ShellException($"{dest}: {CommandSpec.Errno.EISDIR}");
        if (ReferenceEquals(existing, node)) return; // two hard links of one file: rename(2) leaves both
        var target = to.Parent!;
        Require(from.Parent!, 2, source);
        Require(target, 2, dest);
        if (node is Dir && from.Parent != target) Require(node, 2, source); // its ".." entry changes
        RequireNotSticky(from.Parent!, node, source);
        if (existing != null) RequireNotSticky(target, existing, dest);
        from.Parent!.Children.Remove(from.Name);
        if (existing != null) UsedBytes -= Unlink(existing);
        target.Children[to.Name] = node;
    }

    // ---------- snapshots ----------

    /// <summary>One entry as a snapshot sees it: a directory, a file holding <paramref name="Text"/> or (binary) <paramref name="Data"/>,
    /// or a symbolic link holding <paramref name="LinkTarget"/>. <paramref name="HardLink"/>: an earlier entry's path that this
    /// one is another name of (the entry still carries the content).</summary>
    internal readonly record struct Entry(string Path, bool IsDirectory, int Mode, string Owner, DateTime Modified, string? Text = null, byte[]? Data = null,
        long? ReportedSize = null, string? LinkTarget = null, string? HardLink = null);

    /// <summary>A detached copy of the tree. Contents (strings and byte arrays, never changed in place) are shared, not copied.</summary>
    internal VirtualFileSystem Clone() => new() { _root = (Dir)CloneNode(_root, new(ReferenceEqualityComparer.Instance)), UsedBytes = UsedBytes, Capacity = Capacity, Umask = Umask };

    /// <summary>Replaces this tree with a copy of <paramref name="source"/>'s, keeping <see cref="Capacity"/>.</summary>
    internal void ReplaceWith(VirtualFileSystem source)
    {
        _root = (Dir)CloneNode(source._root, new(ReferenceEqualityComparer.Instance));
        UsedBytes = source.UsedBytes;
    }

    /// <param name="copies">Nodes with several names already copied, so hard links stay one node.</param>
    static Node CloneNode(Node n, Dictionary<Node, Node> copies)
    {
        if (n.Nlink > 1 && copies.TryGetValue(n, out var done)) return done;
        Node copy = n switch
        {
            FileNode f => new FileNode { Content = f.Content, Data = f.Data, ReportedSize = f.ReportedSize, Size = f.Size },
            LinkNode l => new LinkNode { Target = l.Target },
            _ => new Dir(),
        };
        (copy.Mode, copy.Owner, copy.Modified, copy.Nlink) = (n.Mode, n.Owner, n.Modified, n.Nlink);
        if (n.Nlink > 1) copies[n] = copy;
        if (n is Dir d) foreach (var (name, child) in d.Children) ((Dir)copy).Children.Add(name, CloneNode(child, copies));
        return copy;
    }

    /// <summary>Every entry, parents before children, "/" first.</summary>
    internal IEnumerable<Entry> Entries()
    {
        var first = new Dictionary<Node, string>(ReferenceEqualityComparer.Instance); // where each hard-linked node was seen first
        var stack = new Stack<(string Path, Node Node)>();
        stack.Push(("/", _root));
        while (stack.Count > 0)
        {
            var (path, n) = stack.Pop();
            string? hard = null;
            if (n.Nlink > 1 && !first.TryAdd(n, path)) hard = first[n];
            yield return EntryOf(n, path, hard);
            if (n is Dir d) foreach (var (name, child) in d.Children.Reverse()) stack.Push((Child(path, name), child));
        }
    }

    static string Child(string dir, string name) => dir == "/" ? "/" + name : dir + "/" + name;

    static Entry EntryOf(Node n, string path, string? hard) => n switch
    {
        FileNode f => new(path, false, f.Mode, f.Owner, f.Modified, f.Data == null ? f.Content : null, f.Data, f.ReportedSize, HardLink: hard),
        LinkNode l => new(path, false, l.Mode, l.Owner, l.Modified, LinkTarget: l.Target, HardLink: hard),
        _ => new(path, true, n.Mode, n.Owner, n.Modified),
    };

    /// <summary>
    /// The paths whose entry (as <see cref="Entries"/> gives it) changed since the tracker last looked, sorted: the same list
    /// as comparing two snapshots, without copying the tree. Each look walks the nodes but builds entries only for nodes
    /// changed since (<see cref="Node.Changed"/>), names a directory gained or replaced, and hard-linked files, whose first
    /// name can move when another name changes.
    /// </summary>
    internal sealed class ChangeTracker
    {
        readonly VirtualFileSystem _fs;
        long _seen = long.MaxValue; // the first look sees everything as new
        readonly Dictionary<string, (Entry Entry, Node Node)> _known = new(StringComparer.Ordinal);
        readonly Dictionary<string, (string Name, Node Node)[]> _names = new(StringComparer.Ordinal); // each directory's names when last looked at

        public ChangeTracker(VirtualFileSystem fs) { _fs = fs; Changes(); }

        public List<string> Changes()
        {
            long now = Volatile.Read(ref ChangeClock);
            var changed = new List<string>();
            var gone = new HashSet<string>(StringComparer.Ordinal); // what was below a removed or replaced name, until seen again
            var first = new Dictionary<Node, string>(ReferenceEqualityComparer.Instance);
            var root = _fs._root;
            Visit(root, "/", _seen == long.MaxValue || !ReferenceEquals(_known["/"].Node, root), changed, gone, first);
            foreach (var p in gone) { _known.Remove(p); _names.Remove(p); changed.Add(p); }
            _seen = now;
            changed.Sort(StringComparer.Ordinal);
            return changed;
        }

        void Visit(Node n, string path, bool moved, List<string> changed, HashSet<string> gone, Dictionary<Node, string> first)
        {
            string? hard = null;
            if (n.Nlink > 1 && !first.TryAdd(n, path)) hard = first[n];
            if (moved || n.Changed > _seen || n.Nlink > 1)
            {
                var e = EntryOf(n, path, hard);
                if (!_known.TryGetValue(path, out var old) || old.Entry != e) changed.Add(path);
                _known[path] = (e, n);
                gone.Remove(path);
            }
            if (n is not Dir d)
            {
                if (moved) _names.Remove(path); // it may have been a directory
                return;
            }
            if (!moved && d.Listed <= _seen)
            {
                foreach (var (name, child) in _names[path]) // the same names as last time, without walking the sorted tree
                    if (child is Dir || child.Changed > _seen || child.Nlink > 1) Visit(child, Child(path, name), false, changed, gone, first);
                return;
            }
            // names gained, lost or replaced: merge the sorted lists of before and now
            var before = _names.GetValueOrDefault(path) ?? [];
            var now = new (string Name, Node Node)[d.Children.Count];
            int i = 0, j = 0;
            foreach (var (name, child) in d.Children)
            {
                now[j++] = (name, child);
                while (i < before.Length && string.CompareOrdinal(before[i].Name, name) < 0) Gone(Child(path, before[i++].Name), gone);
                bool had = i < before.Length && before[i].Name == name, same = had && ReferenceEquals(before[i].Node, child);
                if (had) i++;
                if (!(moved || !same || child is Dir || child.Changed > _seen || child.Nlink > 1)) continue;
                var p = Child(path, name);
                if (had && (moved || !same)) Gone(p, gone); // replaced: what was below it is gone unless seen again
                Visit(child, p, moved || !same, changed, gone, first);
            }
            while (i < before.Length) Gone(Child(path, before[i++].Name), gone);
            _names[path] = now;
        }

        void Gone(string path, HashSet<string> gone)
        {
            gone.Add(path);
            if (_names.TryGetValue(path, out var names)) foreach (var (name, _) in names) Gone(Child(path, name), gone);
        }
    }

    /// <summary>Adds a snapshot entry ("/" sets the root's attributes). Its parent must already be a directory, reached without
    /// symbolic links (so an entry can't land anywhere but its own path), and the name free.</summary>
    internal void Put(Entry e)
    {
        if (e.Path == "/" && !e.IsDirectory) throw new ShellException("/: Not a directory");
        if (e.LinkTarget is { } t && (t.Length is 0 or > 4096 || t.Contains('\0'))) throw new ShellException($"{e.Path}: bad link target");
        Node? Plain(string path)
        {
            Node cur = _root;
            foreach (var seg in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                if (cur is not Dir d || !d.Children.TryGetValue(seg, out cur!)) return null;
            return cur;
        }
        if (e.HardLink != null)
        {
            if (Plain(e.HardLink) is not { } same || same is Dir) throw new ShellException($"{e.Path}: hard link to {e.HardLink}, which is not an earlier file");
            if (Plain(GetParent(e.Path)) is not Dir dir) throw new ShellException($"{e.Path}: parent is not a directory");
            if (!dir.Children.TryAdd(GetName(e.Path), same)) throw new ShellException($"{e.Path}: {CommandSpec.Errno.EEXIST}");
            same.Nlink++;
            return;
        }
        Node node = e.IsDirectory ? (e.Path == "/" ? _root : new Dir())
            : e.LinkTarget != null ? new LinkNode { Target = e.LinkTarget }
            : new FileNode { Content = e.Text ?? "", Data = e.Data, ReportedSize = e.ReportedSize, Size = e.Data?.Length ?? System.Text.Encoding.UTF8.GetByteCount(e.Text ?? "") };
        node.Mode = e.Mode;
        node.Owner = e.Owner;
        node.Modified = e.Modified;
        if (e.Path == "/") return;
        if (Plain(GetParent(e.Path)) is not Dir parent) throw new ShellException($"{e.Path}: parent is not a directory");
        if (!parent.Children.TryAdd(GetName(e.Path), node)) throw new ShellException($"{e.Path}: {CommandSpec.Errno.EEXIST}");
        UsedBytes += TreeSize(node);
    }

    /// <summary>Changes permission bits. While restricted, only the owner may do this.</summary>
    public void Chmod(string path, int mode)
    {
        var node = Find(path, out var why) ?? throw new ShellException($"cannot access '{path}': {why}");
        if (Restricted && node.Owner != RunAs) throw new ShellException($"changing permissions of '{path}': {CommandSpec.Errno.EPERM}");
        node.Mode = mode & 0xFFF;
    }

    /// <summary>Changes the owner (and, with <paramref name="recursive"/>, everything below). Unrestricted callers only.</summary>
    public void SetOwner(string path, string owner, bool recursive = false)
    {
        var node = Find(path, out var why) ?? throw new ShellException($"cannot access '{path}': {why}");
        if (Restricted) throw new ShellException($"changing ownership of '{path}': {CommandSpec.Errno.EPERM}");
        void Apply(Node n)
        {
            n.Owner = owner;
            if (recursive && n is Dir d) foreach (var c in d.Children.Values) Apply(c);
        }
        Apply(node);
    }
}
