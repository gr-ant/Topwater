using Topwater;

namespace Topwater.Tests;

/// <summary>The simulated machine's clock is one zone (UTC): date, file mtimes and uptime must agree, not leak the host's local time.</summary>
public class BashCompatClock
{
    [Fact] // file mtime (via date -r) and date now are the same clock; a host-local base would be hours off
    public Task MtimeMatchesDate() => BashCompat.Check(
        "touch f; now=$(date +%s); m=$(date -r f +%s); d=$(( now - m )); if [ $d -ge -2 ] && [ $d -le 2 ]; then echo ok; else echo \"skew=$d\"; fi",
        "ok\n", 0, true);

    [Fact] // uptime's clock column is UTC too, so its hour matches date -u
    public Task UptimeHourMatchesDate() => BashCompat.Check(
        "h1=$(date -u +%H); h2=$(uptime | grep -oE '^ *[0-9]{2}' | tr -d ' '); [ \"$h1\" = \"$h2\" ] && echo ok || echo \"$h1 vs $h2\"",
        "ok\n", 0, true);

    [Fact] // free's sizes follow its unit options (the corpus can't hold them: real memory differs)
    public Task FreePrintsTheSameMemoryInTheUnitAskedFor() => BashCompat.Check(
        "free -m | sed -n 2p; free -b | sed -n 3p; free -h | sed -n 2p; free -h --si | sed -n 2p; free -g -tw | tail -1; free --kilo | sed -n 3p",
        "Mem:            7827        1945        3891          12        2149        5613\nSwap:     2147479552           0  2147479552\nMem:           7.6Gi       1.9Gi       3.8Gi        12Mi       2.1Gi       5.5Gi\nMem:            8.2G        2.0G        4.1G         12M        2.3G        5.9G\nTotal:             9           1           5\nSwap:        2147479           0     2147479\n", 0, true);
}
