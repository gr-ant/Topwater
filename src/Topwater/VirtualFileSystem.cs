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
/// shortcut: text-only files, no groups, no locking; add them if binary data, group permissions or concurrent shells are needed.
/// </summary>
public sealed class VirtualFileSystem
{
    abstract class Node
    {
        public DateTime Modified = DateTime.Now;
        public string Owner = "root";
        public int Mode;
    }
    sealed class Dir : Node { public readonly SortedDictionary<string, Node> Children = new(StringComparer.Ordinal); }
    sealed class FileNode : Node { public string Content = ""; }

    public const int DefaultFileMode = 0b110_100_100; // 0644
    public const int DefaultDirMode = 0b111_101_101;  // 0755

    readonly Dir _root = new() { Mode = DefaultDirMode };

    /// <summary>User the agent's commands run as, or null for unrestricted access (your app).</summary>
    public string? RunAs { get; set; }

    bool Restricted => RunAs is not null and not "root";

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

    Node? Find(string path)
    {
        Node cur = _root;
        foreach (var seg in Normalize("/", path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cur is not Dir d || !d.Children.TryGetValue(seg, out var next)) return null;
            cur = next;
        }
        return cur;
    }

    Dir ParentDir(string path)
    {
        var parent = GetParent(path);
        return Find(parent) switch
        {
            Dir d => d,
            null => throw new ShellException($"{path}: No such file or directory"),
            _ => throw new ShellException($"{path}: Not a directory"),
        };
    }

    bool Allowed(Node n, int bit) => !Restricted || ((n.Mode >> (n.Owner == RunAs ? 6 : 0)) & bit) != 0;

    void Require(Node n, int bit, string path)
    {
        if (!Allowed(n, bit)) throw new ShellException($"{path}: Permission denied");
    }

    public bool Exists(string path) => Find(path) != null;
    public bool IsDirectory(string path) => Find(path) is Dir;
    public bool IsFile(string path) => Find(path) is FileNode;
    public bool CanRead(string path) => Find(path) is { } n && Allowed(n, 4);
    public bool CanWrite(string path) => Find(path) is { } n && Allowed(n, 2);
    public bool CanExecute(string path) => Find(path) is { } n && Allowed(n, 1);

    public FileStat Stat(string path) => Find(path) switch
    {
        Dir d => new(true, 4096, d.Modified, d.Mode, d.Owner),
        FileNode f => new(false, System.Text.Encoding.UTF8.GetByteCount(f.Content), f.Modified, f.Mode, f.Owner),
        _ => throw new ShellException($"{path}: No such file or directory"),
    };

    public string ReadAllText(string path)
    {
        switch (Find(path))
        {
            case FileNode f: Require(f, 4, path); return f.Content;
            case Dir: throw new ShellException($"{path}: Is a directory");
            default: throw new ShellException($"{path}: No such file or directory");
        }
    }

    public void WriteAllText(string path, string content) => Write(path, content, append: false);
    public void AppendAllText(string path, string content) => Write(path, content, append: true);

    void Write(string path, string content, bool append)
    {
        path = Normalize("/", path);
        if (path == "/dev/null" && Find(path) != null) return;
        var dir = ParentDir(path);
        var name = GetName(path);
        if (dir.Children.TryGetValue(name, out var existing))
        {
            if (existing is not FileNode f) throw new ShellException($"{path}: Is a directory");
            Require(f, 2, path);
            f.Content = append ? f.Content + content : content;
            f.Modified = DateTime.Now;
        }
        else
        {
            Require(dir, 2, path);
            dir.Children[name] = new FileNode { Content = content, Owner = RunAs ?? dir.Owner, Mode = DefaultFileMode };
        }
        dir.Modified = DateTime.Now;
    }

    public void Touch(string path)
    {
        if (Find(path) is { } n)
        {
            Require(n, 2, path);
            n.Modified = DateTime.Now;
        }
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
        dir.Children[GetName(path)] = new Dir { Owner = RunAs ?? dir.Owner, Mode = DefaultDirMode };
        dir.Modified = DateTime.Now;
    }

    /// <summary>Entry names in a directory, sorted ordinally.</summary>
    public IReadOnlyList<string> List(string path)
    {
        switch (Find(path))
        {
            case Dir d: Require(d, 4, path); return d.Children.Keys.ToList();
            case FileNode: throw new ShellException($"{path}: Not a directory");
            default: throw new ShellException($"{path}: No such file or directory");
        }
    }

    public void Delete(string path, bool recursive = false)
    {
        path = Normalize("/", path);
        if (path == "/") throw new ShellException("it is dangerous to operate recursively on '/'");
        var node = Find(path) ?? throw new ShellException($"{path}: No such file or directory");
        if (node is Dir d && d.Children.Count > 0 && !recursive) throw new ShellException($"{path}: Directory not empty");
        var parent = ParentDir(path);
        Require(parent, 2, path);
        if (node is Dir tree) RequireTreeWritable(tree, path);
        parent.Children.Remove(GetName(path));
    }

    void RequireTreeWritable(Dir d, string path)
    {
        if (d.Children.Count == 0) return;
        Require(d, 2, path);
        foreach (var (name, child) in d.Children)
            if (child is Dir sub) RequireTreeWritable(sub, path + "/" + name);
    }

    public void Copy(string source, string dest)
    {
        var node = Find(source) ?? throw new ShellException($"{source}: No such file or directory");
        Require(node, 4, source);
        dest = Normalize("/", dest);
        if (node is Dir && (dest + "/").StartsWith(Normalize("/", source) + "/"))
            throw new ShellException($"cannot copy a directory, '{source}', into itself, '{dest}'");
        var target = Find(dest);
        if (target is Dir && node is FileNode) throw new ShellException($"{dest}: Is a directory");
        var parent = ParentDir(dest);
        if (target != null) Require(target, 2, dest); else Require(parent, 2, dest);
        parent.Children[GetName(dest)] = Clone(node, RunAs ?? parent.Owner);
    }

    public void Move(string source, string dest)
    {
        source = Normalize("/", source);
        dest = Normalize("/", dest);
        var node = Find(source) ?? throw new ShellException($"{source}: No such file or directory");
        if ((dest + "/").StartsWith(source + "/")) throw new ShellException($"cannot move '{source}' to a subdirectory of itself, '{dest}'");
        if (Find(dest) is Dir) throw new ShellException($"{dest}: Is a directory");
        var target = ParentDir(dest);
        var from = ParentDir(source);
        Require(from, 2, source);
        Require(target, 2, dest);
        from.Children.Remove(GetName(source));
        target.Children[GetName(dest)] = node;
    }

    /// <summary>Changes permission bits. While restricted, only the owner may do this.</summary>
    public void Chmod(string path, int mode)
    {
        var node = Find(path) ?? throw new ShellException($"cannot access '{path}': No such file or directory");
        if (Restricted && node.Owner != RunAs) throw new ShellException($"changing permissions of '{path}': Operation not permitted");
        node.Mode = mode & 0xFFF;
    }

    /// <summary>Changes the owner (and, with <paramref name="recursive"/>, everything below). Unrestricted callers only.</summary>
    public void SetOwner(string path, string owner, bool recursive = false)
    {
        var node = Find(path) ?? throw new ShellException($"cannot access '{path}': No such file or directory");
        if (Restricted) throw new ShellException($"changing ownership of '{path}': Operation not permitted");
        void Apply(Node n)
        {
            n.Owner = owner;
            if (recursive && n is Dir d) foreach (var c in d.Children.Values) Apply(c);
        }
        Apply(node);
    }

    static Node Clone(Node n, string owner)
    {
        if (n is FileNode f) return new FileNode { Content = f.Content, Owner = owner, Mode = f.Mode };
        var copy = new Dir { Owner = owner, Mode = n.Mode };
        foreach (var (k, v) in ((Dir)n).Children) copy.Children[k] = Clone(v, owner);
        return copy;
    }
}
