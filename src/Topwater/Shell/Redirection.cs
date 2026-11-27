// Shell: redirections, file descriptors and the output writers. See docs/architecture.md.
using System.Globalization;
using System.Text;
using Topwater.Parsing;

namespace Topwater;

public sealed partial class Shell
{
    // ---------- writers ----------

    /// <summary>Buffers a command's output (a pipeline stage, $(...), the call's stdout); past the string limit the call stops.</summary>
    internal class CappedWriter(Shell shell, long? limit = null) : StringWriter
    {
        readonly long _limit = limit ?? shell.Options.MaxStringBytes;
        protected Shell Owner => shell; // for subclasses, so they don't capture the shell a second time
        protected virtual void Full() => throw shell.TooLarge("output", "write to a file in smaller pieces or filter earlier in the pipeline");
        void Check(long adding) { if (GetStringBuilder().Length + adding > _limit) Full(); }
        public override void Write(char value) { Check(1); base.Write(value); }
        public override void Write(string? value) { Check(value?.Length ?? 0); base.Write(value); }
        public override void Write(char[] buffer, int index, int count) { Check(count); base.Write(buffer, index, count); }
        public override void Write(ReadOnlySpan<char> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override void Write(StringBuilder? value) { Check(value?.Length ?? 0); base.Write(value); }
        public override void WriteLine(ReadOnlySpan<char> buffer) { Check(buffer.Length + CoreNewLine.Length); base.WriteLine(buffer); }
        public override void WriteLine(StringBuilder? value) { Check((value?.Length ?? 0) + CoreNewLine.Length); base.WriteLine(value); }
    }

    /// <summary>A redirect's file: buffered, but written through every megabyte, so the filesystem's cap (not the string limit) bounds it.</summary>
    sealed class FileSink(Shell shell, string path) : CappedWriter(shell, 1 << 20)
    {
        public string Path { get; } = path;

        protected override void Full()
        {
            var text = ToString();
            GetStringBuilder().Clear(); // dropped even if the disk is full, so a loop writing to a full disk doesn't grow the buffer
            Owner.Fs.AppendAllText(Path, text);
        }

        public override void Write(string? value)
        {
            if (value is not { Length: >= 1 << 20 }) { base.Write(value); return; }
            Full(); // a big write (cat of a big file) goes straight to the file instead of through the buffer
            Owner.Fs.AppendAllText(Path, value);
        }
    }

    /// <summary>A file opened by `exec >file`: written through, so later commands in the same call can read it.</summary>
    sealed class FileAppender(VirtualFileSystem fs, string path) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => fs.AppendAllText(path, value.ToString());
        public override void Write(string? value) { if (!string.IsNullOrEmpty(value)) fs.AppendAllText(path, value); }
        public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
    }

    /// <summary>A shell's stdout or stderr as `exec` sees it: what it points at can be changed for the rest of the shell.</summary>
    internal sealed class SwitchWriter(TextWriter target) : TextWriter
    {
        public TextWriter Target { get; set; } = target;
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => Target.Write(value);
        public override void Write(string? value) => Target.Write(value);
        public override void Write(char[] buffer, int index, int count) => Target.Write(buffer, index, count);
    }

    // ---------- redirections ----------

    async Task<Io?> ApplyRedirectsAsync(List<Redirect> redirects, Io io, List<FileSink> sinks, CancellationToken ct)
    {
        foreach (var r in redirects)
        {
            try
            {
                switch (r.Op)
                {
                    case "<<" or "<<<" or "<":
                        var input = await OpenInputAsync(r, io, ct);
                        io = r.Fd >= 3 ? io with { Fds = new(io.Fds ?? []) { [r.Fd] = input } } : io with { Stdin = input };
                        break;
                    case ">&" or "<&":
                        var target = await ExpandSingleAsync(r.Target!, io, ct);
                        object dup = target == "-" ? (r.Op == "<&" ? new StdinBuffer("") : TextWriter.Null)
                            : int.TryParse(target, out var n) && Descriptor(n, io) is { } d ? d
                            : throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.RedirectFailed, ("target", target), ("reason", int.TryParse(target, out _) ? CommandSpec.Errno.EBADF : CommandSpec.Diagnostics.AmbiguousRedirect)));
                        io = WithFd(io, r.Fd, dup);
                        break;
                    default: // > >> >| &> &>>
                        var w = await OpenOutputAsync(r, io, ct, path => { var sink = new FileSink(this, path); sinks.Add(sink); return sink; });
                        io = r.Op.StartsWith('&') ? io with { Out = w, Err = w } : WithFd(io, r.Fd, w);
                        break;
                }
            }
            catch (ShellException e)
            {
                io.Err.WriteLine($"{ErrPrefix}{e.Message}");
                return null;
            }
        }
        return io;
    }

    async Task<StdinBuffer> OpenInputAsync(Redirect r, Io io, CancellationToken ct) => r.Op switch
    {
        "<<" => new StdinBuffer(await ExpandSingleAsync(r.Here!.Body, io, ct)),
        "<<<" => new StdinBuffer(await ExpandSingleAsync(r.Target!, io, ct) + "\n"),
        _ => await ReadRedirectAsync(r, io, ct),
    };

    async Task<StdinBuffer> ReadRedirectAsync(Redirect r, Io io, CancellationToken ct)
    {
        var target = await ExpandSingleAsync(r.Target!, io, ct);
        if (_mounts.Items.Count > 0) await _mounts.BeforeAsync("<", [], target);
        try { return new StdinBuffer(Fs.ReadPipeText(ResolvePath(target))); }
        catch (ShellException e) { throw new ShellException($"{target}: {e.Message[(e.Message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]}"); }
    }

    /// <summary>The writer for > >> >| &amp;> &amp;>>: /dev/null, /dev/stdout, /dev/stderr, or a file made by <paramref name="open"/>.</summary>
    async Task<TextWriter> OpenOutputAsync(Redirect r, Io io, CancellationToken ct, Func<string, TextWriter> open)
    {
        var target = await ExpandSingleAsync(r.Target!, io, ct);
        var path = ResolvePath(target);
        if (path == "/dev/null") return TextWriter.Null;
        if (path is "/dev/stderr" or "/dev/stdout") return path == "/dev/stderr" ? io.Err : io.Out;
        if (path.StartsWith("/dev/fd/") && int.TryParse(path[8..], out var fd) && Descriptor(fd, io) is TextWriter fw) return fw;
        if (Fs.IsDirectory(path)) throw new ShellException($"{target}: Is a directory");
        if (Noclobber && r.Op is ">" or "&>" && Fs.IsFile(path)) throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.Noclobber, ("target", target)));
        if (r.Op.EndsWith(">>") && _mounts.Items.Count > 0) await _mounts.BeforeAsync(">>", [], target);
        try { if (!r.Op.EndsWith(">>") || !Fs.Exists(path)) Fs.WriteAllText(path, ""); } // truncate on open, like bash
        catch (ShellException e) { throw new ShellException($"{target}: {e.Message[(e.Message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]}"); }
        return open(path);
    }

    /// <summary>What fd n refers to: 0 a StdinBuffer, 1 and 2 writers, 3+ whatever `exec` or this command opened; null if closed.</summary>
    object? Descriptor(int n, Io io) => n switch
    {
        0 => io.Stdin, 1 => io.Out, 2 => io.Err,
        _ => io.Fds?.GetValueOrDefault(n) ?? Fds.GetValueOrDefault(n),
    };

    /// <summary>
    /// `exec` with only redirections: they last for the rest of the shell. fds 1 and 2 are retargeted through the
    /// SwitchWriter each (sub)shell starts with; files opened this way are written through as text arrives.
    /// shortcut: inside a command that has its own redirections (`{ exec >f; } 2>x`) the change is ignored.
    /// </summary>
    async Task<int> ExecRedirectsAsync(List<Redirect> redirects, Io io, CancellationToken ct)
    {
        foreach (var r in redirects)
        {
            try
            {
                object? target;
                if (r.Op is "<<" or "<<<" or "<") target = await OpenInputAsync(r, io, ct);
                else if (r.Op is ">&" or "<&")
                {
                    var t = await ExpandSingleAsync(r.Target!, io, ct);
                    target = t == "-" ? null : int.TryParse(t, out var n) && Descriptor(n, io) is { } d ? (d is SwitchWriter sw ? sw.Target : d)
                        : throw new ShellException(CommandSpec.Format(CommandSpec.Diagnostics.RedirectFailed, ("target", t), ("reason", CommandSpec.Errno.EBADF)));
                }
                else target = await OpenOutputAsync(r, io, ct, path => new FileAppender(Fs, path));
                int named = r.FdVariable == null ? r.Fd
                    : target == null ? (int.TryParse(GetVariable(r.FdVariable), out var open) ? open : -1) // {fd}>&- closes the fd it names
                    : Enumerable.Range(10, 100).First(n => !Fds.ContainsKey(n));
                if (r.FdVariable != null && target != null) SetVar(r.FdVariable, named.ToString(CultureInfo.InvariantCulture));
                foreach (var fd in r.Op.StartsWith('&') ? [1, 2] : new[] { named })
                {
                    if (fd < 0) continue;
                    if (fd == 0) io.Stdin.Reset(target is StdinBuffer sb ? sb.ReadAll() : "");
                    else if (fd <= 2) { if ((fd == 1 ? io.Out : io.Err) is SwitchWriter sw) sw.Target = target as TextWriter ?? TextWriter.Null; }
                    else if (target == null) Fds.Remove(fd);
                    else Fds[fd] = target;
                }
            }
            catch (ShellException e)
            {
                io.Err.WriteLine($"{ErrPrefix}{e.Message}");
                return 1;
            }
        }
        return 0;
    }

    /// <summary>`read -u N` / `mapfile -u N`: the input opened on fd N by `exec N&lt;file`, or null.</summary>
    internal StdinBuffer? InputFd(int fd) => Fds.GetValueOrDefault(fd) as StdinBuffer;

    static Io WithFd(Io io, int fd, object target) => fd switch
    {
        0 => target is StdinBuffer sb ? io with { Stdin = sb } : io,
        1 => target is TextWriter w ? io with { Out = w } : io,
        2 => target is TextWriter w ? io with { Err = w } : io,
        _ => io with { Fds = new(io.Fds ?? []) { [fd] = target } },
    };
}
