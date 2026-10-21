using WslCare.Core.Collectors;

namespace WslCare.Core.Health;

/// <summary>
/// One full run's look at Windows Defender's engine (E15.S10): when it was taken, how long the script ran, and the reading it
/// printed — or why there is none (the script was not started, ran past its ceiling, exited non-zero, found no <c>MsMpEng</c>, or
/// printed a line that does not parse).
/// </summary>
public sealed record DefenderSample(DateTimeOffset SampledAt, Reading<TimeSpan> Elapsed, Reading<DefenderReading> Read)
{
    /// <summary>The whole sample unavailable for one reason.</summary>
    public static DefenderSample Unavailable(DateTimeOffset at, string reason) =>
        new(at, Reading.Missing<TimeSpan>(reason), Reading.Missing<DefenderReading>(reason));
}
