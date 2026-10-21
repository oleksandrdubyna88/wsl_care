namespace WslCare.TestSupport;

/// <summary>
/// The Defender CPU script's answer MEASURED on 2026-10-16 (E15.S10; <c>src_daemon/tests/fixtures/health/defender-cpu-2026-10-16</c>,
/// whose <c>SOURCE.txt</c> says how): the one line research/2026-10-16_defender_cpu_cost.md prints.
/// </summary>
public static class DefenderCpuFixture
{
    public const string Name = "defender-cpu-2026-10-16";

    public const string AnswerFile = "powershell-defender-cpu.out";

    public static string Read(string file)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "health", Name, file);
        return File.Exists(path) ? File.ReadAllText(path) : throw new FileNotFoundException($"the Defender CPU fixture should have been copied to {path}; does the project link ../fixtures?");
    }

    /// <summary>The measured answer.</summary>
    public static string Answer => Read(AnswerFile);
}
