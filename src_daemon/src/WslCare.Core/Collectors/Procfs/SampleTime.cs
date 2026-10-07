using WslCare.Core.Files;
using WslCare.Core.Hosting;

namespace WslCare.Core.Collectors.Procfs;

/// <summary>An instant on both clocks: the wall clock a person reads, and the system's monotonic clock that no step moves.</summary>
/// <remarks>Moved here from A18's CPU history (plan E14 S1) so a collector that keeps CPU readings across runs — the MCP servers'
/// ledger — does not depend on the Actions layer (the same reason as the E7.S2d coai code round's finding 1).</remarks>
public readonly record struct SampleTime(DateTimeOffset Wall, long MonotonicMs)
{
    /// <summary>Now, on <paramref name="clock"/>: its wall clock and its timestamp (the system's monotonic clock on Linux).</summary>
    public static SampleTime Of(TimeProvider clock) => new(clock.GetUtcNow(), (long)clock.GetElapsedTime(0, clock.GetTimestamp()).TotalMilliseconds);
}

/// <summary>This boot's identity: a pid and its start ticks name one process only within one boot.</summary>
public static class BootIdentity
{
    /// <summary>This boot's id (<c>/proc/sys/kernel/random/boot_id</c>, under the layout's <c>/proc</c>); empty when unknown.</summary>
    public static string Read(LinuxHostPaths paths, IFileSystem files) =>
        ProcText.Read(files, $"{paths.ProcRoot}/sys/kernel/random/boot_id").ValueOr(string.Empty).Trim();
}
