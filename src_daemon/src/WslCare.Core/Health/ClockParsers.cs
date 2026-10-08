using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;

namespace WslCare.Core.Health;

/// <summary>timesyncd's last NTP sample as <c>timedatectl timesync-status</c> prints it: the server and the offset.</summary>
/// <param name="OffsetSeconds">The offset timesyncd measured at its last poll — positive when the distro was behind.</param>
public sealed record TimesyncSample(string Server, double OffsetSeconds);

/// <summary>
/// The parsers of PLAN_windows_time_guard.md — pure, each shape a unit test over output captured on 2026-10-08
/// (research/2026-10-08_windows_time_stopped.md).
/// </summary>
public static class ClockParsers
{
    /// <summary>The probe's two TAGGED lines (D1), wherever they stand; unavailable when the probe printed neither (an older
    /// probe, or a fake that knows three lines).</summary>
    public static Reading<WindowsTimeService> TimeService(IReadOnlyList<string> lines)
    {
        var status = Tagged(lines, HealthCommands.TimeServiceStatusTag);
        var startType = Tagged(lines, HealthCommands.TimeServiceStartTypeTag);
        return status is null && startType is null
            ? Reading.Missing<WindowsTimeService>("the Windows clock probe printed no w32time line")
            : Reading.Of(new WindowsTimeService(status ?? string.Empty, startType ?? string.Empty));
    }

    private static string? Tagged(IReadOnlyList<string> lines, string tag) =>
        lines.FirstOrDefault(l => l.StartsWith(tag, StringComparison.Ordinal)) is { } line ? line[tag.Length..].Trim() : null;

    /// <summary>The ONE <c>Date:</c> header of a <c>curl --head</c> answer (RFC 1123, <c>Thu, 08 Oct 2026 08:17:08 GMT</c>);
    /// unavailable when there is none, more than one, or it is not an instant.</summary>
    public static Reading<DateTimeOffset> HttpDate(string stdout)
    {
        var dates = ProcText.Lines(stdout).Select(l => l.Trim()).Where(l => l.StartsWith("date:", StringComparison.OrdinalIgnoreCase)).ToList();
        return dates.Count == 1 && DateTimeOffset.TryParseExact(dates[0][5..].Trim(), "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)
            ? Reading.Of(at.ToUniversalTime())
            : Reading.Missing<DateTimeOffset>(dates.Count == 0 ? "the HEAD answer carried no Date header" : $"the HEAD answer's Date header is not one RFC 1123 instant: \"{string.Join(" | ", dates)}\"");
    }

    /// <summary><c>timedatectl timesync-status</c>: <c>Server: 185.125.190.57 (ntp.ubuntu.com)</c> and <c>Offset: -18.401ms</c>.</summary>
    public static Reading<TimesyncSample> Timesync(string stdout)
    {
        var values = ProcText.Lines(stdout).Select(l => l.Split(':', 2)).Where(p => p.Length == 2).ToDictionary(p => p[0].Trim(), p => p[1].Trim(), StringComparer.Ordinal);
        return values.TryGetValue("Offset", out var offset) && SystemdTimespan.Seconds(offset) is { } seconds
            ? Reading.Of(new TimesyncSample(values.GetValueOrDefault("Server", string.Empty), seconds))
            : Reading.Missing<TimesyncSample>($"timedatectl timesync-status printed no readable Offset line: \"{Cut(stdout)}\"");
    }

    /// <summary>The monotonic stamps (seconds since boot) of <c>journalctl --output=short-monotonic</c> lines:
    /// <c>[ 6057.123456] host systemd-journald[62]: Time jumped backwards, rotating.</c> A line without one is skipped.</summary>
    public static IReadOnlyList<double> MonotonicStamps(IReadOnlyList<string> lines) =>
        [.. lines.SelectMany(Stamp)];

    private static IEnumerable<double> Stamp(string line)
    {
        var trimmed = line.TrimStart();
        var close = trimmed.IndexOf(']', StringComparison.Ordinal);
        return trimmed.StartsWith('[') && close > 1 && double.TryParse(trimmed[1..close].Trim(), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var seconds)
            ? [seconds]
            : [];
    }

    private static string Cut(string text) => text.Trim().Length <= QuoteChars ? text.Trim() : text.Trim()[..QuoteChars] + "...";

    private static int QuoteChars => Config.Tuning.Current.Int(Config.ConfigKeys.Records.MaxReasonChars);
}

/// <summary>systemd's printed time spans (<c>format_timespan</c>): an optional sign, then <c>&lt;number&gt;&lt;unit&gt;</c> words —
/// <c>-18.401ms</c>, <c>+2h 420.512ms</c>, <c>1min 3.5s</c>, <c>335us</c>.</summary>
public static class SystemdTimespan
{
    private static readonly (string Unit, double Seconds)[] Units =
    [
        ("month", 2_629_800), ("min", 60), ("ms", 1e-3), ("us", 1e-6), ("μs", 1e-6), ("y", 31_557_600), ("w", 604_800), ("d", 86_400), ("h", 3_600), ("s", 1),
    ];

    /// <summary>The span in seconds, or <c>null</c> when <paramref name="text"/> is not one.</summary>
    public static double? Seconds(string text)
    {
        var value = text.Trim();
        var sign = value.StartsWith('-') ? -1 : 1;
        var words = value.TrimStart('+', '-').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var parts = words.Select(Word).ToList();
        return words.Length > 0 && parts.All(p => p is not null) ? sign * parts.Sum(p => p!.Value) : null;
    }

    private static double? Word(string word)
    {
        var digits = word.TakeWhile(c => char.IsAsciiDigit(c) || c == '.').Count();
        var unit = Units.FirstOrDefault(u => u.Unit == word[digits..]);
        return digits > 0 && unit.Unit is not null && double.TryParse(word[..digits], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n)
            ? n * unit.Seconds
            : null;
    }
}
