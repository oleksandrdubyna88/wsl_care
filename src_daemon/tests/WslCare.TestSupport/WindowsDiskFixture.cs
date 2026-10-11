using System.Globalization;

namespace WslCare.TestSupport;

/// <summary>
/// The Windows disk script's answer CAPTURED on 2026-10-10 (E15.S1; <c>src_daemon/tests/fixtures/health/windows-disk-2026-10-10</c>,
/// whose <c>SOURCE.txt</c> says how and what was redacted): the script <c>WindowsDiskCommands.Script</c> builds under the default
/// configuration, run by Windows PowerShell 5.1 unelevated on the owner's machine.
/// </summary>
public static class WindowsDiskFixture
{
    public const string Name = "windows-disk-2026-10-10";

    public const string AnswerFile = "powershell-windows-disk.out";

    public static string Read(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "health", Name, file);
        return File.Exists(path) ? File.ReadAllText(path) : throw new FileNotFoundException($"the Windows disk fixture should have been copied to {path}; does the project link ../fixtures?");
    }

    /// <summary>The captured answer.</summary>
    public static string Answer => Read(AnswerFile);

    public static DateTimeOffset CapturedAt =>
        DateTimeOffset.Parse(Read("captured-at.txt").Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
}
