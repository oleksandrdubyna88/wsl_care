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
    /// <c>CLOCK_MONOTONIC</c> is in the distro.</summary>
    long UnbiasedMilliseconds();
}

/// <summary>A boot that reads nothing — what every host off Windows and a host built by a test hold: without a boot id the ledger
/// answers "no baseline" and the window measures.</summary>
public sealed class UnreadWindowsBoot(string why) : IWindowsBoot
{
    public Reading<string> BootId() => Reading.Missing<string>(why);

    public long UnbiasedMilliseconds() => 0;
}

/// <summary>Where the Windows side's MCP CPU ledger lives and what reads it (E14 S7b.1): S1's ledger, its place, and the boot.</summary>
public sealed record WindowsCpuLedger(IFileSystem Files, McpCpuLedgerPlace Place, IWindowsBoot Boot)
{
    /// <summary>No ledger: every instance is measured across the window, and nothing is recorded.</summary>
    public static WindowsCpuLedger None(IFileSystem files, string why) => new(files, new McpCpuLedgerPlace.None(why), new UnreadWindowsBoot(why));
}
