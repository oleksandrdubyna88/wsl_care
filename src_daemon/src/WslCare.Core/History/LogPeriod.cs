using System.Globalization;

namespace WslCare.Core.History;

/// <summary>The UTC days a <c>logs</c> / <c>runs</c> answer covers, both ends included (plan §7.4: this run, today,
/// yesterday, a date, a range). A run belongs to the UTC day it STARTED on — the day its run id and its detail folder carry.</summary>
public sealed record LogPeriod(DateOnly From, DateOnly To, string Label)
{
    /// <summary>The longest range one answer covers: the records are kept 90 days, a year leaves room and bounds the read.</summary>
    public const int MaxDays = 366;

    public const string Today = "today";

    public const string Yesterday = "yesterday";

    private const string DayFormat = "yyyy-MM-dd";

    public bool Contains(DateTimeOffset instant) => DateOnly.FromDateTime(instant.UtcDateTime) is var day && day >= From && day <= To;

    /// <summary><c>today</c>, <c>yesterday</c>, <c>yyyy-MM-dd</c> or <c>yyyy-MM-dd..yyyy-MM-dd</c>, against the UTC day of
    /// <paramref name="now"/>; refused with the legal shapes otherwise.</summary>
    public static PeriodParse Parse(string text, DateTimeOffset now)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        return text switch
        {
            Today => new PeriodParse.Parsed(new LogPeriod(today, today, Today)),
            Yesterday => new PeriodParse.Parsed(new LogPeriod(today.AddDays(-1), today.AddDays(-1), Yesterday)),
            _ when text.Contains("..", StringComparison.Ordinal) => Range(text),
            _ => Day(text) is { } day ? new PeriodParse.Parsed(new LogPeriod(day, day, text)) : Refuse(text),
        };
    }

    public string FromText => From.ToString(DayFormat, CultureInfo.InvariantCulture);

    public string ToText => To.ToString(DayFormat, CultureInfo.InvariantCulture);

    private static PeriodParse Range(string text)
    {
        var parts = text.Split("..");
        if (parts.Length != 2 || Day(parts[0]) is not { } from || Day(parts[1]) is not { } to)
        {
            return Refuse(text);
        }

        if (to < from)
        {
            return new PeriodParse.Refused($"the period {text} ends before it starts");
        }

        return to.DayNumber - from.DayNumber + 1 > MaxDays
            ? new PeriodParse.Refused($"the period {text} is longer than {MaxDays} days")
            : new PeriodParse.Parsed(new LogPeriod(from, to, text));
    }

    private static DateOnly? Day(string text) =>
        text.Length == DayFormat.Length && DateOnly.TryParseExact(text, DayFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var day) ? day : null;

    private static PeriodParse.Refused Refuse(string text) =>
        new($"the period \"{text}\" is not today, yesterday, a UTC date (yyyy-MM-dd) or a range (yyyy-MM-dd..yyyy-MM-dd)");
}

/// <summary>What reading a period produced.</summary>
public abstract record PeriodParse
{
    private PeriodParse()
    {
    }

    public sealed record Parsed(LogPeriod Period) : PeriodParse;

    public sealed record Refused(string Reason) : PeriodParse;
}
