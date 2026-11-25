using Topwater;

namespace Topwater.Tests;

/// <summary>bc checks that are not recorded tables; the recorded GNU bc 1.07.1 cases are in tests/corpus/bc.jsonl.</summary>
public class BashCompatBc
{
    // A runaway loop is interruptible: the eval loop checks the cancellation token, so `timeout` stops `while(1){}`
    // (exit 124) within the second. The separate 5 s per-bc wall-clock cap is exercised the same way; it is not
    // asserted here because a 5 s CPU spin in the test suite would make the timing-sensitive LimitTests flaky.
    [Fact]
    public Task RunawayLoopIsInterruptible() =>
        BashCompat.Check("printf '%b\\n' 'while(1){}' | timeout 0.3 bc; echo \"rc=$?\"", "rc=124\n", 0, quiet: true);

    // bc --version prints only the version line, with no FSF/GPL copyright notice.
    [Fact]
    public Task Version() => BashCompat.Check("bc --version", "bc 1.07.1\n", 0, quiet: true);
}
