using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;

using C = WslCare.Core.Health.DefenderCpuCommands;

namespace WslCare.Core.Health;

/// <summary>
/// The parser of the Defender script's one tagged line (<see cref="DefenderCpuCommands"/>) — pure, so every shape is a unit test. The
/// line is read BY TAG; <c>defender.none=</c> says no <c>MsMpEng</c> runs and <c>defender.error=</c> names the exception that stopped the
/// read. A line whose fields do not parse — a count, a sign, a decimal point, a pid past <see cref="int.MaxValue"/> — is no reading,
/// never a reading of 0.
/// </summary>
public static class DefenderParsers
{
    /// <summary>Why a run that found no <c>MsMpEng</c> has no reading.</summary>
    public const string NotRunning = "MsMpEng is not running";

    public static Reading<DefenderReading> Parse(string stdout)
    {
        var lines = ProcText.Lines(stdout).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (ValuesOf(lines, C.Tag) is [var value, ..])
        {
            return Fields(value);
        }

        if (ValuesOf(lines, C.NoneTag).Any())
        {
            return Reading.Missing<DefenderReading>(NotRunning);
        }

        return ValuesOf(lines, C.ErrorTag) is [var error, ..]
            ? Reading.Missing<DefenderReading>($"the Defender script could not read MsMpEng's counters: {error}")
            : Reading.Missing<DefenderReading>($"the Defender script printed no {C.Tag.TrimEnd('=')} line");
    }

    /// <summary>What follows <paramref name="tag"/> on each line that starts with it. <c>defender=</c> is not a prefix of
    /// <c>defender.none=</c> or <c>defender.error=</c>: the character after the word differs.</summary>
    private static List<string> ValuesOf(IEnumerable<string> lines, string tag) =>
        [.. lines.Where(l => l.StartsWith(tag, StringComparison.Ordinal)).Select(l => l[tag.Length..])];

    /// <summary><c>cpu|timestamp|pid|start</c>: four whole non-negative numbers, the pid within an <see cref="int"/>.</summary>
    private static Reading<DefenderReading> Fields(string value) => Numbers(value.Split('|')) switch
    {
        [var cpu, var timestamp, <= int.MaxValue and var pid, var start] => Reading.Of(new DefenderReading(cpu, timestamp, (int)pid, start)),
        _ => Reading.Missing<DefenderReading>($"the Defender script's line \"{C.Tag}{value}\" does not parse (cpu|timestamp|pid|start, whole numbers)"),
    };

    /// <summary>The four fields as numbers; empty unless there are exactly four and every one is a whole non-negative number.</summary>
    private static long[] Numbers(string[] fields) =>
        fields is [_, _, _, _] && fields.All(IsNumber) ? [.. fields.Select(Number)] : [];

    private static bool IsNumber(string field) => long.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static long Number(string field) => long.Parse(field, NumberStyles.None, CultureInfo.InvariantCulture);
}
