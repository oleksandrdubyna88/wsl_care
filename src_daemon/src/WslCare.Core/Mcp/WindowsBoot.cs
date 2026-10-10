using WslCare.Core.Collectors;
using WslCare.Core.Files;

namespace WslCare.Core.Mcp;

/// <summary>
/// Windows' boot identity and monotonic clock for the CPU ledger (E14 S7b.1) — a seam: the real one is <see cref="Win32Boot"/>, a test
/// scripts its own. READ-ONLY by construction: it reads one registry value and one clock.
/// </summary>
public interface IWindowsBoot
{
    /// <summary>This boot's identity as the ledger names it (<c>windows-&lt;BootId&gt;</c>), or why it cannot be read.</summary>
    Reading<string> BootId();

    /// <summary>Milliseconds since boot that do NOT advance while the host sleeps — the ledger's denominator, as Linux's
    /// <c>CLOCK_MONOTONIC</c> is in the distro — or why it could not be read (never 0 for "unread", coai code round 1bc694ea).</summary>
    Reading<long> UnbiasedMilliseconds();
}

/// <summary>A boot that reads nothing — what every host off Windows and a host built by a test hold: without a boot id the ledger
/// answers "no baseline" and the window measures.</summary>
public sealed class UnreadWindowsBoot(string why) : IWindowsBoot
{
    public Reading<string> BootId() => Reading.Missing<string>(why);

    public Reading<long> UnbiasedMilliseconds() => Reading.Missing<long>(why);
}

/// <summary>Whether the Windows side's MCP sample keeps a CPU ledger (E14 S7b.1) — a closed set, no null (coai code round 1bc694ea):
/// <see cref="Kept"/> names S1's ledger, its place and the boot; <see cref="None"/> says why there is none (every instance is then
/// measured across the window, and nothing is recorded).</summary>
public abstract record WindowsCpuLedger
{
    private WindowsCpuLedger()
    {
    }

    /// <summary>S1's ledger at <paramref name="Place"/>, read through <paramref name="Files"/>, keyed by <paramref name="Boot"/>.</summary>
    public sealed record Kept(IFileSystem Files, McpCpuLedgerPlace Place, IWindowsBoot Boot) : WindowsCpuLedger;

    /// <summary>No ledger, and why.</summary>
    public sealed record None(string Reason) : WindowsCpuLedger;
}
