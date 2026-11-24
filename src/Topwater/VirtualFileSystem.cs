namespace Topwater;

/// <summary>Thrown by commands and the filesystem; becomes "cmd: message" on stderr with the given exit code.</summary>
public class ShellException(string message, int exitCode = 1) : Exception(message)
{
    public int ExitCode { get; } = exitCode;
}

public readonly record struct FileStat(bool IsDirectory, long Size, DateTime Modified);

/// <summary>
/// In-memory Unix-style filesystem. All paths are absolute or resolved against "/".
/// Apps can "mount" data for the agent by writing files, e.g. <c>fs.WriteAllText("/data/orders.json", json)</c>.
/// shortcut: text-only files and no locking; add byte content / a lock if binary data or concurrent shells are needed.
/// </summary>
public sealed class VirtualFileSystem
{
    abstract class Node { public DateTime Modified = DateTime.Now; }
    sealed class Dir : Node { public readonly SortedDictionary<string, Node> Children = new(StringComparer.Ordinal); }
    sealed class FileNode : Node { public string Content = ""; }

    readonly Dir _root = new();

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

    public bool Exists(string path) => Find(path) != null;
    public bool IsDirectory(string path) => Find(path) is Dir;
    public bool IsFile(string path) => Find(path) is FileNode;

    public FileStat Stat(string path) => Find(path) switch
    {
        Dir d => new(true, 4096, d.Modified),
        FileNode f => new(false, System.Text.Encoding.UTF8.GetByteCount(f.Content), f.Modified),
        _ => throw new ShellException($"{path}: No such file or directory"),
    };

    public string ReadAllText(string path) => Find(path) switch
    {
        FileNode f => f.Content,
        Dir => throw new ShellException($"{path}: Is a directory"),
        _ => throw new ShellException($"{path}: No such file or directory"),
    };

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
            f.Content = append ? f.Content + content : content;
            f.Modified = DateTime.Now;
        }
        else dir.Children[name] = new FileNode { Content = content };
        dir.Modified = DateTime.Now;
    }

    public void Touch(string path)
    {
        if (Find(path) is { } n) n.Modified = DateTime.Now;
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
        dir.Children[GetName(path)] = new Dir();
        dir.Modified = DateTime.Now;
    }

    /// <summary>Entry names in a directory, sorted ordinally.</summary>
    public IReadOnlyList<string> List(string path) => Find(path) switch
    {
        Dir d => d.Children.Keys.ToList(),
        FileNode => throw new ShellException($"{path}: Not a directory"),
        _ => throw new ShellException($"{path}: No such file or directory"),
    };

    public void Delete(string path, bool recursive = false)
    {
        path = Normalize("/", path);
        if (path == "/") throw new ShellException("it is dangerous to operate recursively on '/'");
        var node = Find(path) ?? throw new ShellException($"{path}: No such file or directory");
        if (node is Dir d && d.Children.Count > 0 && !recursive) throw new ShellException($"{path}: Directory not empty");
        ParentDir(path).Children.Remove(GetName(path));
    }

    public void Copy(string source, string dest)
    {
        var node = Find(source) ?? throw new ShellException($"{source}: No such file or directory");
        dest = Normalize("/", dest);
        if (node is Dir && (dest + "/").StartsWith(Normalize("/", source) + "/"))
            throw new ShellException($"cannot copy a directory, '{source}', into itself, '{dest}'");
        if (Find(dest) is Dir && node is FileNode) throw new ShellException($"{dest}: Is a directory");
        ParentDir(dest).Children[GetName(dest)] = Clone(node);
    }

    public void Move(string source, string dest)
    {
        source = Normalize("/", source);
        dest = Normalize("/", dest);
        var node = Find(source) ?? throw new ShellException($"{source}: No such file or directory");
        if ((dest + "/").StartsWith(source + "/")) throw new ShellException($"cannot move '{source}' to a subdirectory of itself, '{dest}'");
        if (Find(dest) is Dir) throw new ShellException($"{dest}: Is a directory");
        var target = ParentDir(dest);
        ParentDir(source).Children.Remove(GetName(source));
        target.Children[GetName(dest)] = node;
    }

    static Node Clone(Node n)
    {
        if (n is FileNode f) return new FileNode { Content = f.Content };
        var copy = new Dir();
        foreach (var (k, v) in ((Dir)n).Children) copy.Children[k] = Clone(v);
        return copy;
    }
}
