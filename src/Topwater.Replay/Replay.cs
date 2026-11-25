using System.Runtime.CompilerServices;

namespace Topwater.Replay;

/// <summary>Thrown by <see cref="Replay.AssertMatches"/>; the message is the replay report.</summary>
public sealed class ReplayMismatchException(ReplayReport report, string message) : Exception(message)
{
    public ReplayReport Report { get; } = report;
}

/// <summary>Recorded sessions as regression tests (works with any test framework: a mismatch throws).</summary>
public static class Replay
{
    /// <summary>The environment variable that turns <see cref="AssertMatches"/> into re-recording: set it to 1 after an intended change.</summary>
    public const string RerecordVariable = "TOPWATER_RERECORD";

    /// <summary>
    /// Replays the recording at <paramref name="path"/> on a fresh Topwater shell and throws <see cref="ReplayMismatchException"/>
    /// if any call returns something else. A relative path is relative to the calling source file, so recordings can sit next to
    /// the tests without being copied to the output folder. A deterministic recording is compared exactly; another with
    /// <see cref="ReplayNormalizers.Volatile"/> unless <paramref name="options"/> says otherwise. Register your app's commands in
    /// <paramref name="configure"/>. With TOPWATER_RERECORD=1 the file is rewritten with what this Topwater returns instead.
    /// </summary>
    public static async Task AssertMatches(string path, Action<Shell>? configure = null, ReplayOptions? options = null, [CallerFilePath] string callerFile = "")
    {
        if (!Path.IsPathRooted(path) && callerFile.Length > 0) path = Path.Combine(Path.GetDirectoryName(callerFile)!, path);
        var recording = Recording.Load(path);
        if (Environment.GetEnvironmentVariable(RerecordVariable) is "1" or "true")
        {
            await using var target = new ShellTarget(recording.CreateShell(configure));
            var updated = await recording.RerecordAsync(target);
            var sidecar = Directory.Exists(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, Path.GetFileNameWithoutExtension(path) + ".snapshots"));
            updated.Save(path, sidecar);
            return;
        }
        options ??= new ReplayOptions { Normalize = recording.IsDeterministic ? null : ReplayNormalizers.Volatile };
        var report = await recording.ReplayAsync(options, configure);
        if (!report.Matches)
            throw new ReplayMismatchException(report, $"{Path.GetFileName(path)}: {report}\nIf the change is intended, run the test again with {RerecordVariable}=1 to update the recording.");
    }
}
