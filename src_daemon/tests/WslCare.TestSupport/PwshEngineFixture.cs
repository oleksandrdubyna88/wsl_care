using System.Globalization;

namespace WslCare.TestSupport;

/// <summary>
/// The Windows clock probe's answer with its pwsh tags, CAPTURED on 2026-10-12 (E15.S1b;
/// <c>src_daemon/tests/fixtures/health/pwsh-engine-2026-10-12</c>, whose <c>SOURCE.txt</c> says how and what was redacted):
/// Windows PowerShell 5.1 unelevated on the owner's machine, which has NO MSI PowerShell 7 — so the capture is the ABSENT
/// shape, and the verified shape is DERIVED from it (<see cref="Verified"/>), said so.
/// </summary>
public static class PwshEngineFixture
{
    public const string Name = "pwsh-engine-2026-10-12";

    public const string AnswerFile = "powershell-clock.out";

    public static string Read(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "health", Name, file);
        return File.Exists(path) ? File.ReadAllText(path) : throw new FileNotFoundException($"the pwsh-engine fixture should have been copied to {path}; does the project link ../fixtures?");
    }

    /// <summary>The captured answer: both folders <c>C:\Program Files</c>, <c>pwsh.msi=absent</c>, an empty signature.</summary>
    public static string Answer => Read(AnswerFile);

    public static DateTimeOffset CapturedAt =>
        DateTimeOffset.Parse(Read("captured-at.txt").Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    /// <summary>The capture with its two pwsh verdict lines replaced — <paramref name="msi"/> and <paramref name="signature"/> as the
    /// probe would print them on another machine. DERIVED from the capture, said so.</summary>
    public static string WithPwsh(string msi, string signature) =>
        Answer.Replace("pwsh.msi=absent", "pwsh.msi=" + msi, StringComparison.Ordinal)
            .Replace("pwsh.signature=", "pwsh.signature=" + signature, StringComparison.Ordinal);

    /// <summary>The VERIFIED shape: a real file, signed by Microsoft — what the same look printed for the Store package's
    /// <c>pwsh.exe</c> on the day of the capture (SOURCE.txt), at the MSI path.</summary>
    public static string Verified => WithPwsh("file", "signed");
}
