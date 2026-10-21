using WslCare.Core.Collectors;

namespace WslCare.Core.Health;

/// <summary>The parser of the Defender script's one tagged line (<see cref="DefenderCpuCommands"/>) — pure, so every shape is a unit test.</summary>
public static class DefenderParsers
{
    /// <summary>Why a run that found no <c>MsMpEng</c> has no reading.</summary>
    public const string NotRunning = "MsMpEng is not running";

    public static Reading<DefenderReading> Parse(string stdout) => Reading.Of(new DefenderReading(0, 0, 0, 0));
}
