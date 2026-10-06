namespace WslCare.Core.Config;

/// <summary>
/// E7.S2c: the limits that depend on each other (plan §15q *E7.S2c — the story*), checked once the layers are merged — a
/// configuration in which they contradict each other is a configuration error naming both keys and the rule, never a run that
/// quietly breaks one of them (a heartbeat slower than its own staleness, a request cap that cannot hold a shown list).
/// </summary>
public static class NumberRules
{
    /// <summary>The bytes one shown name needs in a request file: 64 hex characters, the JSON quotes and the separator.</summary>
    public const int BytesPerShownName = 67;

    /// <summary>The wedged-after time must be at least this many heartbeats: one late write is not a wedge.</summary>
    public const int HeartbeatsBeforeWedged = 3;

    /// <summary>The margin <c>systemctl stop</c>'s ceiling keeps above the unit's own stop timeout.</summary>
    public const int StopCeilingMarginSeconds = 30;

    private const long HoursPerDay = TimeSpan.TicksPerDay / TimeSpan.TicksPerHour;

    private sealed record Rule(ConfigKey.IntKey Named, Func<EffectiveConfig, bool> Holds, Func<EffectiveConfig, string> Says);

    private static readonly Rule[] Rules =
    [
        new(ConfigKeys.Running.WedgedAfterSeconds,
            c => c.Int(ConfigKeys.Running.WedgedAfterSeconds) >= HeartbeatsBeforeWedged * c.Int(ConfigKeys.Running.HeartbeatSeconds),
            c => $"{ConfigKeys.Running.WedgedAfterSeconds.Name} ({c.Int(ConfigKeys.Running.WedgedAfterSeconds)}) must be at least {HeartbeatsBeforeWedged} × {ConfigKeys.Running.HeartbeatSeconds.Name} ({c.Int(ConfigKeys.Running.HeartbeatSeconds)}): one late heartbeat is not a wedged run"),
        new(ConfigKeys.Requests.MaxRead,
            c => c.Int(ConfigKeys.Requests.MaxRead) >= c.Int(ConfigKeys.Requests.MaxQueued),
            c => $"{ConfigKeys.Requests.MaxRead.Name} ({c.Int(ConfigKeys.Requests.MaxRead)}) must be at least {ConfigKeys.Requests.MaxQueued.Name} ({c.Int(ConfigKeys.Requests.MaxQueued)}): a reader must see every queued request"),
        new(ConfigKeys.Act.MaxListBytes,
            c => c.Int(ConfigKeys.Act.MaxListBytes) >= (long)BytesPerShownName * c.Int(ConfigKeys.Act.MaxShownNames),
            c => $"{ConfigKeys.Act.MaxListBytes.Name} ({c.Int(ConfigKeys.Act.MaxListBytes)}) must hold {ConfigKeys.Act.MaxShownNames.Name} ({c.Int(ConfigKeys.Act.MaxShownNames)}) names of {BytesPerShownName} bytes"),
        new(ConfigKeys.Logs.MaxRangeDays,
            c => c.Int(ConfigKeys.Logs.MaxRangeDays) >= c.Int(ConfigKeys.Runs.HistoryRetentionDays),
            c => $"{ConfigKeys.Logs.MaxRangeDays.Name} ({c.Int(ConfigKeys.Logs.MaxRangeDays)}) must be at least {ConfigKeys.Runs.HistoryRetentionDays.Name} ({c.Int(ConfigKeys.Runs.HistoryRetentionDays)}): every kept day can be asked for"),
        new(ConfigKeys.Systemd.UnitStopTimeoutSeconds,
            c => c.Int(ConfigKeys.Systemd.UnitStopTimeoutSeconds) >= c.Int(ConfigKeys.Units.StopTimeoutSeconds) + StopCeilingMarginSeconds,
            c => $"{ConfigKeys.Systemd.UnitStopTimeoutSeconds.Name} ({c.Int(ConfigKeys.Systemd.UnitStopTimeoutSeconds)}) must be at least {ConfigKeys.Units.StopTimeoutSeconds.Name} ({c.Int(ConfigKeys.Units.StopTimeoutSeconds)}) + {StopCeilingMarginSeconds}: systemctl stop waits for the unit's own stop"),
        new(ConfigKeys.FileLocks.LockJitterMaxMilliseconds,
            c => c.Int(ConfigKeys.FileLocks.LockJitterMaxMilliseconds) > c.Int(ConfigKeys.FileLocks.LockJitterMinMilliseconds),
            c => $"{ConfigKeys.FileLocks.LockJitterMaxMilliseconds.Name} must be above {ConfigKeys.FileLocks.LockJitterMinMilliseconds.Name}"),
        new(ConfigKeys.Timer.PeriodHours,
            c => HoursPerDay % c.Int(ConfigKeys.Timer.PeriodHours) == 0,
            c => $"{ConfigKeys.Timer.PeriodHours.Name} ({c.Int(ConfigKeys.Timer.PeriodHours)}) must divide the {HoursPerDay} hours of a day: the timer's calendar (00/<hours>) restarts at midnight, so any other period would leave one short interval a day"),
        new(ConfigKeys.Events.RetryMaxSeconds,
            c => c.Int(ConfigKeys.Events.RetryMaxSeconds) >= c.Int(ConfigKeys.Events.RetryFirstSeconds),
            c => $"{ConfigKeys.Events.RetryMaxSeconds.Name} must be at least {ConfigKeys.Events.RetryFirstSeconds.Name}"),
    ];

    /// <summary>Every rule <paramref name="config"/> breaks, each said once, against the layer that set the key it names.</summary>
    public static IReadOnlyList<ConfigError> Broken(EffectiveConfig config, IReadOnlyList<ConfigLayerFile> layers) =>
        [.. Rules.Where(r => !r.Holds(config)).Select(r => new ConfigError(LayerOf(config, r.Named, layers), 0, r.Says(config)))];

    private static ConfigLayerFile LayerOf(EffectiveConfig config, ConfigKey key, IReadOnlyList<ConfigLayerFile> layers) =>
        layers.LastOrDefault(l => l.Layer == config.Entry(key).Layer) ?? layers[^1];
}
