using WslCare.Core.Collectors;
using WslCare.Core.Hosting;
using WslCare.Core.Files;

namespace WslCare.Core.Health;

/// <summary>What one full run made of Windows Defender's CPU: the usage since the previous full run, the week it joins, and — when the
/// reading was not recorded in the ledger — why.</summary>
public sealed record DefenderOutcome(Reading<DefenderUsage> Usage, Reading<DefenderWeek> Week, string NotRecorded);

/// <summary>E15.S10: the step between the script's reading and the verdict — the ledger read, the two-point rule, the ledger append, the week.</summary>
public static class DefenderCpuRun
{
    public static DefenderOutcome Apply(IHostPaths paths, IFileSystem files, DefenderSample sample, bool mayRecord) =>
        new(Reading.Missing<DefenderUsage>("stub"), Reading.Missing<DefenderWeek>("stub"), string.Empty);
}
