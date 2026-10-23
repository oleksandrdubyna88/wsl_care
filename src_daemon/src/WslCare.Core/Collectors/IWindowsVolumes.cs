using WslCare.Core.Health;

namespace WslCare.Core.Collectors;

/// <summary>
/// The lettered Windows volumes, read NATIVELY (E19.S2, PLAN_disk_hygiene_windows_E19.md §3 decision 1) — a seam: the real one is
/// <see cref="WindowsVolumes"/> (<c>DriveInfo</c>, no process), a test scripts its own. READ-ONLY by construction. What the Windows watch
/// samples every <c>mcpWatchdog.periodMinutes</c> into <c>volumes.json</c>, and what the Windows <c>status</c> reads live when that
/// sample is missing, unreadable or too old.
/// </summary>
public interface IWindowsVolumes
{
    /// <summary>Every kept volume, in drive-letter order — or why the table could not be listed. A volume whose size or free space could not
    /// be read is IN the list with that figure unavailable and its reason, never 0.</summary>
    Reading<IReadOnlyList<WindowsVolume>> Read();
}

/// <summary>A table that reads nothing — what every host off Windows and a host built by a test hold: the real table is wired only by the
/// Windows binary's own host, so a test's answer never depends on the drives of the machine it runs on (and the distro's binary, which
/// judges the volumes from its full run's PowerShell reading, never reads a Windows drive natively).</summary>
public sealed class UnreadWindowsVolumes(string why) : IWindowsVolumes
{
    public Reading<IReadOnlyList<WindowsVolume>> Read() => Reading.Missing<IReadOnlyList<WindowsVolume>>(why);
}
