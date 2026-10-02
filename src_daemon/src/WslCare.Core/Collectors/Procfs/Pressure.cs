using System.Globalization;

namespace WslCare.Core.Collectors.Procfs;

/// <summary>One PSI line: the share of wall time (percent) some or all tasks stalled, averaged over 10 s,
/// 60 s and 300 s, and the total stall time in microseconds.</summary>
public sealed record PressureLine(double Avg10, double Avg60, double Avg300, long TotalMicroseconds);

/// <summary>One <c>/proc/pressure/{memory,io,cpu}</c> file: the <c>some</c> line, and the <c>full</c>
/// line where the kernel writes one (older kernels have no <c>full</c> for cpu).</summary>
public sealed record Pressure(PressureLine Some, Reading<PressureLine> Full);

/// <summary><c>/proc/pressure/*</c> (plan §4.1: the primary "VM struggles" signal).</summary>
public static class PressureFile
{
    public static Reading<Pressure> Parse(string text, string path)
    {
        var lines = ProcText.Lines(text).ToDictionary(l => l.Split(' ')[0], ParseLine, StringComparer.Ordinal);
        var some = lines.GetValueOrDefault("some") ?? Reading.Missing<PressureLine>($"{path} has no well-formed \"some\" line");
        var full = lines.GetValueOrDefault("full") ?? Reading.Missing<PressureLine>($"{path} has no \"full\" line");
        return some.Map(s => new Pressure(s, full));
    }

    private static Reading<PressureLine> ParseLine(string line)
    {
        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)
            .Select(f => f.Split('=', 2))
            .Where(kv => kv.Length == 2)
            .ToDictionary(kv => kv[0], kv => kv[1], StringComparer.Ordinal);
        return Reading.Combine(
            Reading.Combine(Number(fields, "avg10"), Number(fields, "avg60"), (a, b) => (a, b)),
            Reading.Combine(Number(fields, "avg300"), Number(fields, "total"), (c, d) => (c, d)),
            (x, y) => new PressureLine(x.a, x.b, y.c, (long)y.d));
    }

    private static Reading<double> Number(IReadOnlyDictionary<string, string> fields, string name) =>
        fields.TryGetValue(name, out var text) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? Reading.Of(value)
            : Reading.Missing<double>($"no {name}= field");
}
