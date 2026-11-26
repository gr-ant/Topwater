using System.Runtime.CompilerServices;

namespace Topwater.Tests;

static class TestHost
{
    // Many tests block a pool thread on a synchronous Run() while xunit runs classes in parallel. On a 2-core runner the
    // pool then grows by about one thread every half second, which would delay the pool work Timed measures. Start with
    // enough threads that timing tests measure Topwater, not thread-pool growth.
    /// <summary>
    /// Runs <paramref name="run"/> on the thread pool and times it there. xunit resumes an awaiting test on its own few
    /// threads (one per core), which tests that block synchronously can hold for many seconds: timed from the test, a 1 s
    /// call limit measured 14-37 s on 2 cores, though the call itself took 1.03 s.
    /// </summary>
    internal static Task<(T Result, TimeSpan Took)> Timed<T>(Func<Task<T>> run) => Task.Run(async () =>
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var result = await run();
        return (result, watch.Elapsed);
    });

    [ModuleInitializer]
    internal static void RaiseThreadPoolMinimum()
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        ThreadPool.SetMinThreads(Math.Max(worker, 64), Math.Max(io, 64));
    }
}
