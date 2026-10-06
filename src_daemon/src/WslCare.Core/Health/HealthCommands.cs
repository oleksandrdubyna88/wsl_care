using WslCare.Core.Config;
using WslCare.Core.Processes;

namespace WslCare.Core.Health;

/// <summary>
/// The two health commands that are neither systemd's nor Docker's, in ONE place — read-only, each with its ceiling:
/// the Windows clock probe (a SLOW process, so only <c>collect</c> starts it — plan §15b #5) and <c>snap list --all</c>
/// for A9's disabled revisions.
/// </summary>
public static class HealthCommands
{
    /// <summary>The name WSL interop reaches PowerShell by from inside the distro; on Windows the same file name.</summary>
    public const string PowerShell = "powershell.exe";

    public const string Snap = "snap";

    /// <summary>
    /// What the probe prints, one per line: Windows' UTC now, the PowerShell process's own start (UTC, the same
    /// clock), and <c>%USERPROFILE%</c>. The first two give the launch latency WITHOUT any skew in it, which is
    /// what lets the skew subtract it (plan §15b #5); the profile is how the distro finds <c>.wslconfig</c> and
    /// Docker Desktop's <c>daemon.json</c> through <c>/mnt</c> without a walk.
    /// </summary>
    public const string ClockScript =
        "[Console]::Out.Write([DateTime]::UtcNow.ToString('o') + \"`n\" + (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o') + \"`n\" + $env:USERPROFILE + \"`n\")";

    /// <summary>Measured 2026-10-02 from WSL Ubuntu: 0.85–1.1 s per start, of which 0.56–0.83 s before the script ran.</summary>
    public static TimeSpan ClockCeiling => Tuning.Current.Seconds(ConfigKeys.Health.WindowsClockTimeoutSeconds);

    public static TimeSpan SnapCeiling => Tuning.Current.Seconds(ConfigKeys.Health.SnapTimeoutSeconds);

    private static int Cap => Tuning.Current.Int(ConfigKeys.Health.OutputCapBytes);

    public static ToolCommand WindowsClock =>
        new(PowerShell, "powershell-clock", ["-NoProfile", "-NonInteractive", "-Command", ClockScript], ClockCeiling, Cap)
        {
            Summary = "powershell.exe (the Windows clock probe)",
        };

    public static ToolCommand SnapList => new(Snap, "snap-list-all", ["list", "--all"], SnapCeiling, Cap);
}
