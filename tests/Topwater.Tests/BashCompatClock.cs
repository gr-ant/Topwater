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
}
