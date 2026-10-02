namespace WslCare.TestSupport;

/// <summary>A clock that answers one instant, UTC, so a test about time asserts against a known value.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public static readonly DateTimeOffset DefaultNow = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    public FixedTimeProvider() : this(DefaultNow)
    {
    }

    public override DateTimeOffset GetUtcNow() => now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}
