// python3: CPython compiled to WebAssembly, run over this machine's files (Wasi.cs). Opt-in: it exists only after the host
// calls AllowPython. See docs/python.md; python/src/topwater/commands/python.py is the reference this mirrors.
using System.Globalization;

namespace Topwater.Python;

/// <summary>What <see cref="PythonShellExtensions.AllowPython"/> takes.</summary>
public sealed record PythonOptions
{
    /// <summary>Bytes of guest memory per call. The interpreter itself starts at about 40 MB; a program past the limit gets MemoryError.</summary>
    public long MaxMemory { get; init; } = 256L << 20;
    /// <summary>A folder holding python.wasm.gz and python314.zip; null (the default) uses the ones this package carries.</summary>
    public string? Artifacts { get; init; }
    /// <summary>A folder of yours where the compiled interpreter is kept between processes, so only the first process pays the compile
    /// (seconds); null compiles once per process. Trust it like your own code: a compiled module is machine code.</summary>
    public string? CacheDir { get; init; }
}

public static class PythonShellExtensions
{
    /// <summary>
    /// Gives the agent python3: CPython 3.14 compiled to WebAssembly, over this machine's files and nothing else (docs/python.md).
    /// Compiles the interpreter on the first use in a process (seconds) and throws <see cref="PythonUnavailableException"/> here when
    /// it can't run. HTTP from Python goes through <see cref="Shell.AllowWeb(WebPolicy)"/>'s policy, or nowhere. A fork keeps it.
    /// </summary>
    public static Shell AllowPython(this Shell shell, PythonOptions? options = null)
    {
        options ??= new PythonOptions();
        Wasi.GetRuntime(options.Artifacts, options.CacheDir); // fails here, in the host's code, when the add-on can't run
        Builtins.RegisterPythonCommand(shell, ctx => Python3Async(ctx, options));
        return shell;
    }

    static string Message(string key, params (string, string)[] values) => CommandSpec.Message("python3", key, values);

    /// <summary>Runs fn on a new thread with a stack large enough for the guest, in this execution context (the agent's user).</summary>
    static Task<T> InThread<T>(Func<T> fn)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { done.SetResult(fn()); }
            catch (Exception e) { done.SetException(e); } // handed to the awaiting command, which throws it there
        }, Wasi.ThreadStack) { IsBackground = true, Name = "topwater-python3" };
        thread.Start();
        return done.Task;
    }

    static readonly WebSession NoNetwork = new(WebSession.NoNetwork);

    /// <summary>The HTTP bridge's sender: each request goes through the shell's web access (its policy, credentials, limits and
    /// recording), one hop at a time; the guest follows redirects itself, each checked again.</summary>
    static Func<string, string, List<KeyValuePair<string, string>>, byte[], TimeSpan, (int, byte[])> Bridge(WebSession? web, DateTime deadline, CancellationToken ct)
    {
        var session = web ?? NoNetwork;
        byte[] Failed(string method, string url, string reason) => System.Text.Encoding.UTF8.GetBytes(Message("httpFailed", ("method", method), ("url", url), ("reason", reason)));
        (int, byte[]) TimedOut(string method, string url, TimeSpan timeout) =>
            (Wasi.HTTP_TIMEOUT, Failed(method, url, CommandSpec.Message("curl", "timeout", ("ms", ((long)timeout.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)), ("bytes", "0"))));

        return (method, url, headers, body, timeout) =>
        {
            // never much past the deadline: a source that hangs must not hold the guest's thread forever
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(ct);
            bound.CancelAfter(Clamp(deadline - DateTime.UtcNow + TimeSpan.FromSeconds(5)));
            try
            {
                var u = WebUrl.Parse(url);
                var r = session.SendAsync(u, method, headers, body.Length > 0 || method is "POST" or "PUT" or "PATCH" ? body : null, timeout, bound.Token).GetAwaiter().GetResult();
                if (r.Truncated) return (Wasi.HTTP_NETWORK, Failed(method, url, CommandSpec.Message("curl", "tooLarge")));
                return (Wasi.HTTP_OK, Wasi.EncodeResponse(r.Status, r.Headers, r.Body));
            }
            catch (WebFailure f)
            {
                // curl's wording for the same failure (BadUrl: badUrl, ...)
                var kind = char.ToLowerInvariant(f.Kind.ToString()[0]) + f.Kind.ToString()[1..];
                var key = f.Blocked != null ? "blocked" : kind;
                var reason = CommandSpec.Commands["curl"].Messages.ContainsKey(key)
                    ? CommandSpec.Message("curl", key, ("reason", f.Blocked ?? ""), ("host", f.Host), ("port", f.Port.ToString(CultureInfo.InvariantCulture)),
                        ("ms", f.Detail is "" ? "0" : f.Detail), ("bytes", "0"), ("max", f.Detail), ("scheme", f.Host), ("request", f.Detail))
                    : kind;
                return (f.Kind == WebError.Timeout ? Wasi.HTTP_TIMEOUT : Wasi.HTTP_NETWORK, Failed(method, url, reason));
            }
            catch (OperationCanceledException) { return TimedOut(method, url, timeout); }
        };
    }

    static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : t;

    static async Task<int> Python3Async(CommandContext ctx, PythonOptions options)
    {
        var sh = ctx.Shell;
        var spec = CommandSpec.Of("python3");
        var args = ctx.Args;
        if (args.Count > 0 && args[0] is "-h" or "-?" or "--help" or "--help-all") // the spec's help, in our words
        {
            ctx.Stdout.Write(spec.Help);
            return 0;
        }
        if (args.Count > 0 && args[0] is "-V" or "--version")
        {
            ctx.Stdout.WriteLine(spec.Version);
            return 0;
        }
        Wasi.Runtime rt;
        try { rt = Wasi.GetRuntime(options.Artifacts, options.CacheDir); }
        catch (PythonUnavailableException e)
        {
            ctx.Stderr.WriteLine("python3: " + Message("unavailable", ("reason", e.Message)));
            return 1;
        }
        var env = sh.Environment.Where(e => e.Key != "PWD" && sh.IsExported(e.Key)).OrderBy(e => e.Key, StringComparer.Ordinal)
            .Append(new("PWD", sh.Cwd)).ToList();
        var deadline = DateTime.UtcNow + Clamp(sh.Options.MaxCallDuration);
        if (sh.CallDeadline < deadline) deadline = sh.CallDeadline;
        var mounts = sh.MountTable;

        // a mounted file or folder the program reached before it was fetched: fetch it now (the guest waits on its thread),
        // unlike other commands, which say "run the command again"
        bool Fetch()
        {
            if (!mounts.Items.Any(m => m.HasMisses)) return false;
            try { return mounts.AfterAsync().Wait(Clamp(deadline - DateTime.UtcNow + TimeSpan.FromSeconds(5))); }
            catch (AggregateException) { return true; } // its failures are kept for the retry to report
        }

        var stdin = ByteText.Encode(ctx.StdinText);
        var guest = new Wasi.Guest(sh.Fs, ["python3", .. args], env, stdin, sh.Options.MaxStringBytes, options.MaxMemory, rt.Stdlib, deadline,
            ctx.CancellationToken, Bridge(sh.Web, deadline, ctx.CancellationToken), mounts.Items.Count > 0 ? Fetch : null);
        Wasi.Outcome outcome;
        try { outcome = await InThread(() => Wasi.Run(rt, guest)); }
        catch (Wasi.OutputFull) { throw sh.TooLarge("output", "write to a file in smaller pieces or filter earlier in the pipeline"); }
        ctx.Stdout.Write(ByteText.Decode(guest.Stdout.ToArray()));
        ctx.Stderr.Write(ByteText.Decode(guest.Stderr.ToArray()));
        foreach (var note in guest.Notes.Distinct()) ctx.Stderr.WriteLine("python3: " + note); // a file a mount had not fetched yet
        foreach (var (path, errno) in guest.WriteBackErrors)
            ctx.Stderr.WriteLine("python3: " + Message("writeBack", ("file", path), ("errno", Wasi.ErrnoText(errno))));
        if (outcome.TimedOut)
        {
            // the call's (or `timeout`'s) limit: ends it as for any command
            ctx.CancellationToken.ThrowIfCancellationRequested();
            sh.CheckDeadline();
            ctx.Stderr.WriteLine("python3: " + Message("stopped"));
            return 1;
        }
        if (outcome.StackOverflow)
        {
            ctx.Stderr.WriteLine("python3: " + Message("stackOverflow"));
            return 1;
        }
        if (outcome.Trap is { } trap)
        {
            ctx.Stderr.WriteLine("python3: " + Message("fatal", ("reason", trap)));
            return 1;
        }
        int code = outcome.Code & 0xFF;
        return code != 0 ? code : guest.WriteBackErrors.Count > 0 ? 1 : 0;
    }
}
