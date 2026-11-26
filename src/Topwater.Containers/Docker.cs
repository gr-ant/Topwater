using System.Diagnostics;
using System.Text;

namespace Topwater.Containers;

/// <summary>Runs the docker CLI. The only place in Topwater.Containers that starts a process.</summary>
public static class Docker
{
    /// <summary>The docker CLI to run. Something CLI-compatible (podman) may work but is untested.</summary>
    public static string Executable { get; set; } = "docker";

    /// <summary>True when the docker CLI runs and reaches a daemon (within 10 seconds).</summary>
    public static Task<bool> IsAvailableAsync() =>
        OffContext.Run(() => IsAvailableCoreAsync());

    static async Task<bool> IsAvailableCoreAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await RunAsync(["version", "--format", "{{.Server.Version}}"], cancellationToken: cts.Token);
            return true;
        }
        catch (Exception e) when (e is ContainerException or OperationCanceledException or System.ComponentModel.Win32Exception) { return false; }
    }

    /// <summary>
    /// Runs docker with <paramref name="args"/> (no shell involved). <paramref name="stdin"/> is written to its input; with
    /// <paramref name="readStdout"/>, its output is streamed to that callback, else returned as text. Throws
    /// <see cref="ContainerException"/> with docker's error output when it exits non-zero. If the callback throws or the
    /// call is cancelled, the process is killed.
    /// </summary>
    internal static async Task<string> RunAsync(IEnumerable<string> args, Func<Stream, Task>? stdin = null, Func<Stream, Task>? readStdout = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            RedirectStandardInput = stdin != null, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new ContainerException($"could not start {Executable}");
        var err = p.StandardError.ReadToEndAsync(CancellationToken.None);
        string output = "";
        try
        {
            var feed = stdin == null ? Task.CompletedTask : Task.Run(async () =>
            {
                try { await stdin(p.StandardInput.BaseStream); }
                finally { p.StandardInput.Close(); }
            }, cancellationToken);
            if (readStdout != null) await readStdout(p.StandardOutput.BaseStream);
            else output = await p.StandardOutput.ReadToEndAsync(cancellationToken);
            await feed;
            await p.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }
        if (p.ExitCode != 0)
            throw new ContainerException($"{Executable} {string.Join(' ', psi.ArgumentList.Take(2))} failed (exit {p.ExitCode}): {(await err).Trim()}");
        return output;
    }
}
