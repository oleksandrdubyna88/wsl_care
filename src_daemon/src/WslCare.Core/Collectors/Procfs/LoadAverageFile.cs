using System.Globalization;

namespace WslCare.Core.Collectors.Procfs;

/// <summary>The 1, 5 and 15-minute load averages.</summary>
public sealed record LoadAverages(double One, double Five, double Fifteen);

/// <summary><c>/proc/loadavg</c> (<c>load1 load5 load15 running/total lastpid</c>) — read by the idle gate and by
/// <c>wsl-care busy</c> (E14 S6, which shows it and judges it not).</summary>
public static class LoadAverageFile
{
    /// <summary><c>/proc/loadavg</c> under <paramref name="paths"/>' procfs root.</summary>
    public static Reading<LoadAverages> Read(Files.IFileSystem files, Hosting.LinuxHostPaths paths) =>
        ProcText.Read(files, $"{paths.ProcRoot}/loadavg").Bind(Parse);

    public static Reading<LoadAverages> Parse(string text) =>
        Reading.Combine(Reading.Combine(Field(text, 0), Field(text, 1), (one, five) => (one, five)), Field(text, 2), (x, fifteen) => new LoadAverages(x.one, x.five, fifteen));

    /// <summary>One average: field 0 is the 1-minute one.</summary>
    public static Reading<double> Field(string text, int field)
    {
        var fields = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > field && double.TryParse(fields[field], NumberStyles.Float, CultureInfo.InvariantCulture, out var load) && load >= 0
            ? Reading.Of(load)
            : Reading.Missing<double>("/proc/loadavg is not \"load1 load5 load15 …\"");
    }
}
