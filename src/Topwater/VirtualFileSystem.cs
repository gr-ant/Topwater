namespace Topwater;

/// <summary>Thrown by commands and the filesystem; becomes "cmd: message" on stderr with the given exit code.</summary>
public class ShellException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <param name="Mode">Unix permission bits, e.g. 0x1ED (0755).</param>
public readonly record struct FileStat(bool IsDirectory, long Size, DateTime Modified, int Mode, string Owner);

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
        public DateTime Modified = DateTime.UtcNow;
        public string Owner = "root";
        public int Mode;
    }
    sealed class Dir : Node { public readonly SortedDictionary<string, Node> Children = new(StringComparer.Ordinal); }
    sealed class FileNode : Node
    {
        string? _text = "";
        System.Text.StringBuilder? _appended; // `>>` writes accumulate here, so append loops stay linear
        public string Content
        {
            // the builder is dropped once read, so a file isn't held twice; the next append copies it once (as the read did)
            get { if (_text == null) { _text = _appended!.ToString(); _appended = null; } return _text; }
            set { _text = value; _appended = null; }
        }
        public byte[]? Data; // set for binary files; Content is then unused
        public long? ReportedSize; // what `ls -l` and `stat` show for a program stub, whose real content is a few bytes
        public long Size; // real bytes held, as counted against the filesystem's capacity
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

    internal const string NoSpace = "No space left on device";

    static long TreeSize(Node n) => n is FileNode f ? f.Size : ((Dir)n).Children.Values.Sum(TreeSize);

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
        if ((mode & 0x200) != 0) chars[9] = chars[9] == 'x' ? 't' : 'T'; // sticky bit (01000)
        return new string(chars);
    }

    Node? Find(string path) => Find(path, out _);

    /// <summary>Walks <paramref name="path"/>; like Linux, each directory passed through needs search (x) permission,
    /// else the result is null with <paramref name="denied"/> set.</summary>
    Node? Find(string path, out bool denied)
    {
        denied = false;
        var user = User;
        Node cur = _root;
        foreach (var seg in Normalize("/", path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur is not Dir d) return null;
            if (!Permits(d, user, 1)) { denied = true; return null; }
            if (!d.Children.TryGetValue(seg, out var next)) return null;
            cur = next;
        }
        return cur;
    }

    static string Missing(bool denied) => denied ? "Permission denied" : "No such file or directory";

    /// <summary>Why <paramref name="path"/> can't be reached: "Permission denied" or "No such file or directory".</summary>
    internal string WhyMissing(string path) => Missing(Find(path, out var denied) == null && denied);

    /// <summary>The directory an entry is created in, removed from or renamed in; it needs search permission too.</summary>
    Dir ParentDir(string path)
    {
        var node = Find(GetParent(path), out var denied);
        if (node is Dir d) { Require(d, 1, path); return d; }
        throw new ShellException($"{path}: {(node == null ? Missing(denied) : "Not a directory")}");
    }

    static bool Permits(Node n, string? user, int bits) => user == null || ((n.Mode >> (n.Owner == user ? 6 : 0)) & bits) == bits;
    bool Allowed(Node n, int bit) => Permits(n, User, bit);

    void Require(Node n, int bit, string path)
    {
        if (!Allowed(n, bit)) throw new ShellException($"{path}: Permission denied");
    }

    /// <summary>In a sticky directory (/tmp) only the entry's owner or the directory's may remove or rename the entry.</summary>
    void RequireNotSticky(Dir dir, Node entry, string path)
    {
        if (User is { } u && (dir.Mode & 0x200) != 0 && entry.Owner != u && dir.Owner != u)
            throw new ShellException($"{path}: Operation not permitted");
    }

    public bool Exists(string path) => Find(path) != null;
    public bool IsDirectory(string path) => Find(path) is Dir;
    public bool IsFile(string path) => Find(path) is FileNode;
    public bool CanRead(string path) => Find(path) is { } n && Allowed(n, 4);
    public bool CanWrite(string path) => Find(path) is { } n && Allowed(n, 2);
    public bool CanExecute(string path) => Find(path) is { } n && Allowed(n, 1);

    public FileStat Stat(string path) => Find(path, out var denied) switch
    {
        Dir d => new(true, 4096, d.Modified, d.Mode, d.Owner),
        FileNode f => new(false, f.ReportedSize ?? f.Data?.Length ?? System.Text.Encoding.UTF8.GetByteCount(f.Content), f.Modified, f.Mode, f.Owner),
        _ => throw new ShellException($"{path}: {Missing(denied)}"),
    };

    public string ReadAllText(string path) => ReadNode(path) is var f && f.Data == null ? f.Content : System.Text.Encoding.UTF8.GetString(f.Data);

    /// <summary>What `cat` and `&lt;` put on a pipe: like <see cref="ReadAllText"/>, but a binary file's bytes survive (see <see cref="ByteText"/>).</summary>
    internal string ReadPipeText(string path) => ReadNode(path) is var f && f.Data == null ? f.Content : ByteText.Decode(f.Data);

    /// <summary>The file's exact bytes (UTF-8 for files written as text).</summary>
    public byte[] ReadAllBytes(string path) => ReadNode(path) is var f && f.Data != null ? (byte[])f.Data.Clone() : f.Raw;

    FileNode ReadNode(string path)
    {
        switch (Find(path, out var denied))
        {
            case FileNode f: Require(f, 4, path); return f;
            case Dir: throw new ShellException($"{path}: Is a directory");
            default: throw new ShellException($"{path}: {Missing(denied)}");
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
        var dir = ParentDir(path);
        var name = GetName(path);
        if (dir.Children.TryGetValue(name, out var existing))
        {
            if (existing is not FileNode f) throw new ShellException($"{path}: Is a directory");
            Require(f, 2, path);
            f.Store(content, data, append, d => Reserve(d, path));
            f.Modified = Clock();
        }
        else
        {
            Require(dir, 2, path);
            var file = new FileNode { Owner = RunAs ?? dir.Owner, Mode = DefaultFileMode, Modified = Clock() };
            file.Store(content, data, false, d => Reserve(d, path));
            dir.Children[name] = file;
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
    internal void SetModifiedTime(string path, DateTime when)
    {
        var node = Find(path, out var denied) ?? throw new ShellException($"{path}: {Missing(denied)}");
        if (Restricted && node.Owner != RunAs) throw new ShellException($"{path}: Operation not permitted");
        node.Modified = when;
    }

    internal void SetReportedSize(string path, long size)
    {
        if (Find(path) is FileNode f) f.ReportedSize = size;
    }

    public void Touch(string path)
    {
        if (Find(path, out var denied) is { } n)
        {
            Require(n, 2, path);
            n.Modified = Clock();
        }
        else if (denied) throw new ShellException($"{path}: Permission denied");
        else WriteAllText(path, "");
    }

    public void CreateDirectory(string path, bool parents = false)
    {
        path = Normalize("/", path);
        if (Find(path) is { } existing)
        {
            if (parents && existing is Dir) return;
            throw new ShellException($"cannot create directory '{path}': File exists");
        }
        if (parents && !Exists(GetParent(path))) CreateDirectory(GetParent(path), true);
        var dir = ParentDir(path);
        Require(dir, 2, path);
        dir.Children[GetName(path)] = new Dir { Owner = RunAs ?? dir.Owner, Mode = DefaultDirMode, Modified = Clock() };
        dir.Modified = Clock();
    }

    /// <summary>Entry names in a directory, sorted ordinally.</summary>
    public IReadOnlyList<string> List(string path)
    {
        switch (Find(path, out var denied))
        {
            case Dir d: Require(d, 4, path); return d.Children.Keys.ToList();
            case FileNode: throw new ShellException($"{path}: Not a directory");
            default: throw new ShellException($"{path}: {Missing(denied)}");
        }
    }

    public void Delete(string path, bool recursive = false)
    {
        path = Normalize("/", path);
        if (path == "/") throw new ShellException("it is dangerous to operate recursively on '/'");
        var node = Find(path, out var denied) ?? throw new ShellException($"{path}: {Missing(denied)}");
        if (node is Dir d && d.Children.Count > 0 && !recursive) throw new ShellException($"{path}: Directory not empty");
        var parent = ParentDir(path);
        if (node is Dir tree) RequireTreeRemovable(tree, path);
        Require(parent, 2, path);
        RequireNotSticky(parent, node, path);
        parent.Children.Remove(GetName(path));
        UsedBytes -= TreeSize(node);
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
    public void Copy(string source, string dest) => Copy(source, dest, (p, _) => throw new ShellException($"{p}: Permission denied"));

    /// <summary>Like `cp -r`: entries the user can't read are skipped and passed to <paramref name="denied"/> (path, is directory).</summary>
    internal void Copy(string source, string dest, Action<string, bool> denied)
    {
        source = Normalize("/", source);
        var node = Find(source, out var blocked) ?? throw new ShellException($"{source}: {Missing(blocked)}");
        dest = Normalize("/", dest);
        if (node is Dir && (dest + "/").StartsWith(source + "/"))
            throw new ShellException($"cannot copy a directory, '{source}', into itself, '{dest}'");
        var target = Find(dest);
        if (target is Dir && node is FileNode) throw new ShellException($"{dest}: Is a directory");
        var parent = ParentDir(dest);
        if (target != null) Require(target, 2, dest); else Require(parent, 2, dest);
        var user = User;
        Node? Clone(Node n, string path)
        {
            if (!Permits(n, user, n is Dir ? 5 : 4))
            {
                denied(path, n is Dir);
                return null;
            }
            if (n is FileNode f) return new FileNode { Content = f.Content, Data = f.Data, Size = f.Size, Owner = RunAs ?? parent.Owner, Mode = f.Mode, Modified = Clock() };
            var copy = new Dir { Owner = RunAs ?? parent.Owner, Mode = n.Mode, Modified = Clock() };
            foreach (var (k, v) in ((Dir)n).Children)
                if (Clone(v, path + "/" + k) is { } c) copy.Children[k] = c;
            return copy;
        }
        if (Clone(node, source) is not { } clone) return;
        // copying onto an existing file writes into it, keeping its owner and mode, as cp does
        if (target is FileNode tf && clone is FileNode cf) { tf.Store(cf.Content, cf.Data, false, d => Reserve(d, dest)); tf.Modified = Clock(); }
        else
        {
            Reserve(TreeSize(clone) - (target == null ? 0 : TreeSize(target)), dest);
            parent.Children[GetName(dest)] = clone;
        }
    }

    public void Move(string source, string dest)
    {
        source = Normalize("/", source);
        dest = Normalize("/", dest);
        var node = Find(source, out var denied) ?? throw new ShellException($"{source}: {Missing(denied)}");
        if ((dest + "/").StartsWith(source + "/")) throw new ShellException($"cannot move '{source}' to a subdirectory of itself, '{dest}'");
        var existing = Find(dest);
        if (existing is Dir) throw new ShellException($"{dest}: Is a directory");
        var target = ParentDir(dest);
        var from = ParentDir(source);
        Require(from, 2, source);
        Require(target, 2, dest);
        if (node is Dir && from != target) Require(node, 2, source); // its ".." entry changes
        RequireNotSticky(from, node, source);
        if (existing != null) RequireNotSticky(target, existing, dest);
        from.Children.Remove(GetName(source));
        if (existing != null) UsedBytes -= TreeSize(existing);
        target.Children[GetName(dest)] = node;
    }

    // ---------- snapshots ----------

    /// <summary>One entry as a snapshot sees it: a directory, or a file holding <paramref name="Text"/> or (binary) <paramref name="Data"/>.</summary>
    internal readonly record struct Entry(string Path, bool IsDirectory, int Mode, string Owner, DateTime Modified, string? Text = null, byte[]? Data = null, long? ReportedSize = null);

    /// <summary>A detached copy of the tree. Contents (strings and byte arrays, never changed in place) are shared, not copied.</summary>
    internal VirtualFileSystem Clone() => new() { _root = (Dir)CloneNode(_root), UsedBytes = UsedBytes, Capacity = Capacity };

    /// <summary>Replaces this tree with a copy of <paramref name="source"/>'s, keeping <see cref="Capacity"/>.</summary>
    internal void ReplaceWith(VirtualFileSystem source)
    {
        _root = (Dir)CloneNode(source._root);
        UsedBytes = source.UsedBytes;
    }

    static Node CloneNode(Node n)
    {
        if (n is FileNode f) return new FileNode { Content = f.Content, Data = f.Data, ReportedSize = f.ReportedSize, Size = f.Size, Mode = f.Mode, Owner = f.Owner, Modified = f.Modified };
        var d = new Dir { Mode = n.Mode, Owner = n.Owner, Modified = n.Modified };
        foreach (var (name, child) in ((Dir)n).Children) d.Children.Add(name, CloneNode(child));
        return d;
    }

    /// <summary>Every entry, parents before children, "/" first.</summary>
    internal IEnumerable<Entry> Entries()
    {
        var stack = new Stack<(string Path, Node Node)>();
        stack.Push(("/", _root));
        while (stack.Count > 0)
        {
            var (path, n) = stack.Pop();
            if (n is FileNode f) { yield return new(path, false, f.Mode, f.Owner, f.Modified, f.Data == null ? f.Content : null, f.Data, f.ReportedSize); continue; }
            yield return new(path, true, n.Mode, n.Owner, n.Modified);
            foreach (var (name, child) in ((Dir)n).Children.Reverse()) stack.Push((path == "/" ? "/" + name : path + "/" + name, child));
        }
    }

    /// <summary>Adds a snapshot entry ("/" sets the root's attributes). Its parent must already be a directory and the name free.</summary>
    internal void Put(Entry e)
    {
        if (e.Path == "/" && !e.IsDirectory) throw new ShellException("/: Not a directory");
        Node node = e.IsDirectory ? (e.Path == "/" ? _root : new Dir())
            : new FileNode { Content = e.Text ?? "", Data = e.Data, ReportedSize = e.ReportedSize, Size = e.Data?.Length ?? System.Text.Encoding.UTF8.GetByteCount(e.Text ?? "") };
        node.Mode = e.Mode;
        node.Owner = e.Owner;
        node.Modified = e.Modified;
        if (e.Path == "/") return;
        if (Find(GetParent(e.Path)) is not Dir parent) throw new ShellException($"{e.Path}: parent is not a directory");
        if (!parent.Children.TryAdd(GetName(e.Path), node)) throw new ShellException($"{e.Path}: File exists");
        UsedBytes += TreeSize(node);
    }

    /// <summary>Changes permission bits. While restricted, only the owner may do this.</summary>
    public void Chmod(string path, int mode)
    {
        var node = Find(path, out var denied) ?? throw new ShellException($"cannot access '{path}': {Missing(denied)}");
        if (Restricted && node.Owner != RunAs) throw new ShellException($"changing permissions of '{path}': Operation not permitted");
        node.Mode = mode & 0xFFF;
    }

    /// <summary>Changes the owner (and, with <paramref name="recursive"/>, everything below). Unrestricted callers only.</summary>
    public void SetOwner(string path, string owner, bool recursive = false)
    {
        var node = Find(path, out var denied) ?? throw new ShellException($"cannot access '{path}': {Missing(denied)}");
        if (Restricted) throw new ShellException($"changing ownership of '{path}': Operation not permitted");
        void Apply(Node n)
        {
            n.Owner = owner;
            if (recursive && n is Dir d) foreach (var c in d.Children.Values) Apply(c);
        }
        Apply(node);
    }
}
