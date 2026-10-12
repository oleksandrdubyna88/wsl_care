using WslCare.Core.Collectors;

namespace WslCare.Core.Processes;

/// <summary>
/// A monotonic clock that does NOT advance while the host sleeps — what <see cref="JobSupervisor"/> counts a worker's deadline on, so a
/// sleep is not run time (WR-D3, PLAN_windows_runner.md §3.3 point 4). The real one is <see cref="Mcp.Win32Boot"/>
/// (<c>QueryUnbiasedInterruptTime</c>); a test scripts its own. Its own seam since the second coai code round on WR-D3 (finding 3): the
/// process supervisor depends on this clock, not on the MCP ledger's boot identity.
/// </summary>
public interface IUnbiasedClock
{
    /// <summary>Milliseconds since boot that do NOT advance while the host sleeps — as Linux's <c>CLOCK_MONOTONIC</c> is in the distro —
    /// or why they could not be read (never 0 for "unread", coai code round 1bc694ea).</summary>
    Reading<long> UnbiasedMilliseconds();
}
