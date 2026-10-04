using System.Globalization;

namespace WslCare.Core.History;

/// <summary>The UTC days a <c>logs</c> / <c>runs</c> answer covers, both ends included (plan §7.4: this run, today,
/// yesterday, a date, a range). A run belongs to the UTC day it STARTED on — the day its run id and its detail folder carry.
/// Or, since E6.S0, a half-open range of INSTANTS (<see cref="Instants"/>, §15j M7): a run belongs to it when it started
/// inside it — how a client asks for a LOCAL day.</summary>
public sealed partial record LogPeriod(DateOnly From, DateOnly To, string Label)
{
    /// <summary>The longest range one answer covers: the records are kept 90 days, a year leaves room and bounds the read.</summary>
    public const int MaxDays = 366;

    public const string Today = "today";

    public const string Yesterday = "yesterday";

    /// <summary>The label of an instant range (§15j M7).</summary>
    public const string InstantsLabel = "instants";

    private const string DayFormat = "yyyy-MM-dd";

    /// <summary>The instant range this period was asked as (<c>--from</c> / <c>--to</c>, half-open, UTC); <c>null</c> for a
    /// period of UTC days.</summary>
    public InstantRange? Instants { get; init; }

    public bool Contains(DateTimeOffset instant) =>
        Instants is { } range
            ? instant >= range.From && instant < range.To
            : DateOnly.FromDateTime(instant.UtcDateTime) is var day && day >= From && day <= To;

    /// <summary>
    /// <c>--from &lt;RFC3339&gt; --to &lt;RFC3339&gt;</c> (§15j M7): a half-open range of INSTANTS, each with its offset spelt
    /// out (<c>Z</c> or <c>±hh:mm</c>) — the extension sends the instants of a LOCAL day's two midnights, so the daemon never
    /// guesses a zone and never binds a bare date to the reading machine's offset (the UTC rule). At most
    /// <see cref="MaxDays"/> long, <c>to</c> after <c>from</c>; refused with the legal shape otherwise.
    /// </summary>
    public static PeriodParse ParseInstants(string from, string to) =>
        Instant(from) is { } start && Instant(to) is { } end
            ? Bounded(start, end)
            : new PeriodParse.Refused($"--from and --to take RFC 3339 instants with their offset spelt out (yyyy-MM-ddTHH:mm:ss[.fffffff] then Z or +hh:mm / -hh:mm); got \"{from}\" and \"{to}\"");

    /// <summary>The range, once both ends read: it ends after it starts and spans at most <see cref="MaxDays"/>; its days are
    /// the UTC days the two instants touch (the end is exclusive).</summary>
    private static PeriodParse Bounded(DateTimeOffset start, DateTimeOffset end) =>
        end <= start ? new PeriodParse.Refused($"the instant range must end after it starts ({start:O} .. {end:O})")
        : end - start > TimeSpan.FromDays(MaxDays) ? new PeriodParse.Refused($"the instant range is longer than {MaxDays} days")
        : new PeriodParse.Parsed(new LogPeriod(DateOnly.FromDateTime(start.UtcDateTime), DateOnly.FromDateTime(end.AddTicks(-1).UtcDateTime), InstantsLabel) { Instants = new InstantRange(start, end) });

    /// <summary>One RFC 3339 instant with its offset spelt out, as UTC; <c>null</c> for anything else — a bare date or a
    /// time without an offset is never bound to the reading machine's zone (the UTC rule).</summary>
    private static DateTimeOffset? Instant(string text) =>
        Rfc3339().IsMatch(text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var instant) ? instant.ToUniversalTime() : null;

    [System.Text.RegularExpressions.GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex Rfc3339();

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
        if (Ends(text) is not var (from, to))
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

    /// <summary>The two days of <c>from..to</c>; <c>null</c> unless both are dates.</summary>
    private static (DateOnly From, DateOnly To)? Ends(string text)
    {
        var parts = text.Split("..");
        return parts.Length == 2 && Day(parts[0]) is { } from && Day(parts[1]) is { } to ? (from, to) : null;
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

/// <summary>A half-open range of instants, both UTC: from inclusive, to exclusive.</summary>
public sealed record InstantRange(DateTimeOffset From, DateTimeOffset To);
