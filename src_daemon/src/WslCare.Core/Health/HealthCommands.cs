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

    public const string Curl = "curl";

    /// <summary>The tag of the probe's line carrying the Windows Time service's state (<c>Running</c>, <c>Stopped</c>, …).</summary>
    public const string TimeServiceStatusTag = "w32time.status=";

    /// <summary>The tag of the probe's line carrying the service's start type (<c>Automatic</c>, <c>Manual</c>, <c>Disabled</c>).</summary>
    public const string TimeServiceStartTypeTag = "w32time.startType=";

    /// <summary>
    /// What the probe prints, one per line: Windows' UTC now, the PowerShell process's own start (UTC, the same
    /// clock), and <c>%USERPROFILE%</c>. The first two give the launch latency WITHOUT any skew in it, which is
    /// what lets the skew subtract it (plan §15b #5); the profile is how the distro finds <c>.wslconfig</c> and
    /// Docker Desktop's <c>daemon.json</c> through <c>/mnt</c> without a walk. Since PLAN_windows_time_guard.md D1 two more
    /// TAGGED lines follow — the Windows Time service's status and start type, read AFTER both instants were taken, so the
    /// latency is unchanged; tagged because an empty value must not shift the lines the parser reads by position.
    /// </summary>
    public const string ClockScript =
        "$n = [DateTime]::UtcNow.ToString('o'); $p = (Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o'); "
        + "$s = Get-Service -Name w32time -ErrorAction SilentlyContinue; "
        + "[Console]::Out.Write($n + \"`n\" + $p + \"`n\" + $env:USERPROFILE + \"`n\" + '" + TimeServiceStatusTag + "' + $s.Status + \"`n\" + '" + TimeServiceStartTypeTag + "' + $s.StartType + \"`n\")";

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

    /// <summary>The clock reference's ceiling (<c>clock.referenceTimeoutSeconds</c>) — curl's own <c>--max-time</c> too.</summary>
    public static int ReferenceSeconds => Tuning.Current.Int(ConfigKeys.Clock.ReferenceTimeoutSeconds);

    /// <summary>
    /// One HEAD to <paramref name="url"/> for its <c>Date</c> header — the independent clock reference
    /// (PLAN_windows_time_guard.md D2). curl runs as root under the timer, so: <c>--disable</c> FIRST (no <c>.curlrc</c> can add
    /// an <c>output</c> or <c>write-out</c>), HTTPS only and no redirect, no cache, and the address after <c>--url</c>, where it
    /// is never read as an option. The template that admits it is <see cref="Processes.Policy.ReadCommandTemplates.ClockReference"/>.
    /// </summary>
    public static ToolCommand ClockReference(string url, int seconds) =>
        new(
            Curl,
            "curl-head-date",
            [
                "--disable", "--silent", "--show-error", "--head", "--max-redirs", "0", "--proto", "=https", "--proto-redir", "=https",
                "--max-time", seconds.ToString(System.Globalization.CultureInfo.InvariantCulture), "--header", "Cache-Control: no-cache", "--url", url,
            ],
            TimeSpan.FromSeconds(seconds),
            Cap)
        {
            Summary = $"curl --head {url} (the clock reference)",
        };
}
