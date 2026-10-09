using System.Globalization;

using WslCare.Core.Collectors;

namespace WslCare.Core.Health;

/// <summary>
/// E14 S7a: the Windows binary's advice when <c>vmmemWSL</c> — the VM as Windows sees it — holds more than
/// <c>wslConfig.vmmemAdviceGb</c>. TEXT ONLY, never acted on, and <c>.wslconfig</c> is never written (owner question Q5). The
/// reclaim setting is advised only when the file was read and does not set it; <c>wsl --shutdown</c> is named as the last resort it
/// is, because it ends every WSL session (coai plan round 2026-10-09, finding 5). PURE.
/// </summary>
public static class VmmemAdvice
{
    private const double BytesPerGibibyte = 1024d * 1024 * 1024;

    private const string Reclaim = "[experimental] autoMemoryReclaim=dropcache";

    private const string Shutdown = " As a last resort, wsl --shutdown returns all of it to Windows, and ends every WSL session and every program running in the distro.";

    /// <summary>The advice; empty when <c>vmmemWSL</c> was not read or holds no more than <paramref name="adviceGb"/> GiB.</summary>
    public static string For(Reading<long> vmmem, Reading<HostMemory> host, Reading<WslConfigAudit> wslConfig, int adviceGb) =>
        vmmem is Reading<long>.Available { Value: var bytes } && bytes > adviceGb * BytesPerGibibyte
            ? Holds(bytes, host) + ReclaimAdvice(wslConfig) + Shutdown
            : string.Empty;

    private static string Holds(long bytes, Reading<HostMemory> host) => host switch
    {
        Reading<HostMemory>.Available { Value: var h } => Invariant($"vmmemWSL holds {bytes / BytesPerGibibyte:0.0} GiB of the host's {h.TotalBytes / BytesPerGibibyte:0.0} GiB."),
        _ => Invariant($"vmmemWSL holds {bytes / BytesPerGibibyte:0.0} GiB."),
    };

    /// <summary>What <c>.wslconfig</c> says about reclaiming page cache — the setting advised only when the file was read without it.</summary>
    private static string ReclaimAdvice(Reading<WslConfigAudit> wslConfig) => wslConfig switch
    {
        Reading<WslConfigAudit>.Available { Value: { Read: true } audit } when Sets(audit) =>
            $" {Reclaim} is already set in {audit.File}, so WSL hands page cache back to Windows on its own.",
        Reading<WslConfigAudit>.Available { Value: { Read: true } audit } =>
            $" WSL returns page cache to Windows only with {Reclaim} in {audit.File}: add {Reclaim} there (shown here, never written by wsl-care).",
        Reading<WslConfigAudit>.Available { Value: var audit } =>
            $" Whether {audit.File} sets {Reclaim} is not known: {string.Join("; ", audit.Warnings)}.",
        _ => $" Whether .wslconfig sets {Reclaim} is not known: {wslConfig.ReasonOrEmpty}.",
    };

    private static bool Sets(WslConfigAudit audit) =>
        audit.Present && audit.Settings.AutoMemoryReclaim.Equals("dropcache", StringComparison.OrdinalIgnoreCase);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
