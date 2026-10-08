using System.Globalization;

namespace WslCare.TestSupport;

/// <summary>
/// The answers CAPTURED on 2026-10-08 for the Windows Time guard (<c>src_daemon/tests/fixtures/health/windows-time-2026-10-08</c>;
/// its <c>SOURCE.txt</c> says how and what was redacted): <c>timedatectl timesync-status</c>, the monotonic journal search of
/// journald's backward jumps, the clock reference's <c>curl --head</c>, and the tagged clock probe.
/// </summary>
public static class TimeGuardFixture
{
    public const string Name = "windows-time-2026-10-08";

    public static string Read(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "health", Name, file);
        return File.Exists(path) ? File.ReadAllText(path) : throw new FileNotFoundException($"the time-guard fixture should have been copied to {path}; does the project link ../fixtures?");
    }

    public static DateTimeOffset CapturedAt =>
        DateTimeOffset.Parse(Read("captured-at.txt").Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>The probe's tagged answer of 2026-10-08 with the service line replaced — the shape the owner saw before the
    /// restart (Stopped, Manual: research/2026-10-08_windows_time_stopped.md O1). DERIVED from the capture, said so.</summary>
    public static string ProbeWithService(string status, string startType) =>
        Read("powershell-clock.out").Replace("w32time.status=Running", "w32time.status=" + status, StringComparison.Ordinal)
            .Replace("w32time.startType=Manual", "w32time.startType=" + startType, StringComparison.Ordinal);
}
