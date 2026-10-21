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

    /// <summary>More than one tagged line: no answer (a forged line ahead of the script's own is not told from it).</summary>
    public const string MoreThanOnce = "the Defender script printed a defender line more than once, so none of it is read";

    /// <summary>The disk parser's precedence (security round, Fable 6): the script's own <c>defender.error=</c> wins over a value; more than one of the
    /// other tagged lines is no answer; one value is read; <c>defender.none=</c> says no MsMpEng runs.</summary>
    public static Reading<DefenderReading> Parse(string stdout)
    {
        var lines = ProcText.Lines(stdout).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        var values = ValuesOf(lines, C.Tag);
        var none = ValuesOf(lines, C.NoneTag);
        return (ValuesOf(lines, C.ErrorTag), values.Count + none.Count) switch
        {
            ([var error, ..], _) => Reading.Missing<DefenderReading>($"the Defender script could not read MsMpEng's counters: {error}"),
            (_, > 1) => Reading.Missing<DefenderReading>(MoreThanOnce),
            _ => values is [var value] ? Fields(value) : Absent(none.Count),
        };
    }

    private static Reading<DefenderReading> Absent(int none) =>
        Reading.Missing<DefenderReading>(none > 0 ? NotRunning : $"the Defender script printed no {C.Tag.TrimEnd('=')} line");

    /// <summary>What follows <paramref name="tag"/> on each line that starts with it. <c>defender=</c> is not a prefix of
    /// <c>defender.none=</c> or <c>defender.error=</c>: the character after the word differs.</summary>
    private static List<string> ValuesOf(IEnumerable<string> lines, string tag) =>
        [.. lines.Where(l => l.StartsWith(tag, StringComparison.Ordinal)).Select(l => l[tag.Length..])];

    /// <summary><c>cpu|timestamp|pid|start</c>: four whole non-negative numbers, the pid within an <see cref="int"/>.</summary>
    private static Reading<DefenderReading> Fields(string value) => Numbers(value.Split('|')) switch
    {
        [var cpu, var timestamp, var pid, var start] when IsReading(timestamp, pid, start) => Reading.Of(new DefenderReading(cpu, timestamp, (int)pid, start)),
        _ => Reading.Missing<DefenderReading>($"the Defender script's line \"{C.Tag}{value}\" does not parse (cpu|timestamp|pid|start, whole numbers)"),
    };

    /// <summary>A reading no clock or process table could not have produced is none (security round, Fable 1): an instant past the FILETIME range or at 0
    /// (the timestamp and the start marker — <see cref="DefenderCpu.IsInstant"/>, the ledger's rule too), a pid of 0 or past an <see cref="int"/>.</summary>
    private static bool IsReading(long timestamp, long pid, long start) =>
        DefenderCpu.IsInstant(timestamp) && DefenderCpu.IsInstant(start) && pid is > 0 and <= int.MaxValue;

    /// <summary>The four fields as numbers; empty unless there are exactly four and every one is a whole non-negative number.</summary>
    private static long[] Numbers(string[] fields) =>
        fields is [_, _, _, _] && fields.All(IsNumber) ? [.. fields.Select(Number)] : [];

    private static bool IsNumber(string field) => long.TryParse(field, NumberStyles.None, CultureInfo.InvariantCulture, out _);

    private static long Number(string field) => long.Parse(field, NumberStyles.None, CultureInfo.InvariantCulture);
}
