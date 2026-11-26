using System.Runtime.CompilerServices;

namespace Topwater.Tests;

static class TestHost
{
    // Many tests block a pool thread on a synchronous Run() while xunit runs classes in parallel. On a 2-core CI runner
    // the pool then grows by about one thread every half second, so the timer that ends a call at its time limit can
    // fire many seconds late (LimitTests saw 13 s for a 1 s limit). Start with enough threads that timing tests measure
    // Topwater, not thread-pool growth.
    [ModuleInitializer]
    internal static void RaiseThreadPoolMinimum()
    {
        ThreadPool.GetMinThreads(out var worker, out var io);
        ThreadPool.SetMinThreads(Math.Max(worker, 64), Math.Max(io, 64));
    }
}
