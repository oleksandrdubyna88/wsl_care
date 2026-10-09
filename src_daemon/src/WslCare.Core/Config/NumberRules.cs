namespace WslCare.Core.Config;

/// <summary>
/// E7.S2c: the limits that depend on each other (plan §15q *E7.S2c — the story*), checked as each layer is merged — a layer
/// whose values contradict each other never puts them in force: the keys THAT layer set for a broken rule go back to the layer
/// below, and the rule is said against that layer (E7.S2b/S2c review C-M1, C-M2) — a machine-layer contradiction is a
/// <see cref="ConfigError"/> (observe-only), a user-layer one a <see cref="ConfigNotice"/> (the value not taken), so a user value
/// in its range can never put ROOT observe-only.
/// </summary>
public static class NumberRules
{
    /// <summary>The bytes one shown name needs in a request file: 64 hex characters, the JSON quotes and the separator.</summary>
    public const int BytesPerShownName = 67;

    /// <summary>What a request file holds besides its shown names: the run id, kind, actions, trigger, times, boot.</summary>
    public const int RequestOverheadBytes = 4096;

    /// <summary>The wedged-after time must be at least this many heartbeats: one late write is not a wedge.</summary>
    public const int HeartbeatsBeforeWedged = 3;

    /// <summary>The margin <c>systemctl stop</c>'s ceiling keeps above the unit's own stop timeout.</summary>
    public const int StopCeilingMarginSeconds = 30;

    /// <summary>One A18 CPU-history entry, both clocks, serialised (measured: 157 072 bytes for 512 entries).</summary>
    public const int BytesPerCpuEntry = 320;

    /// <summary>The history a day of runs can write: 64 lines of 2 KiB (6 timer runs and the buttons of a busy day).</summary>
    public const int HistoryBytesPerDay = 128 * 1024;

    /// <summary>The margin a command's own ceiling keeps under the longest wait a request accepts, and a watchdog above it.</summary>
    public const int CeilingMarginSeconds = 60;

    /// <summary>The margin a timer run's limit keeps above its derived worst case.</summary>
    public const int RunMarginMinutes = 10;

    private const long HoursPerDay = TimeSpan.TicksPerDay / TimeSpan.TicksPerHour;
    private const long SecondsPerMinute = TimeSpan.TicksPerMinute / TimeSpan.TicksPerSecond;
    private const long SecondsPerHour = TimeSpan.TicksPerHour / TimeSpan.TicksPerSecond;

    /// <summary>One rule: the keys it couples (the first named in its sentence), whether it holds, and what it says.</summary>
    public sealed record Rule(IReadOnlyList<ConfigKey.IntKey> Keys, Func<EffectiveConfig, bool> Holds, Func<EffectiveConfig, string> Says);

    private static int I(EffectiveConfig c, ConfigKey.IntKey key) => c.Int(key);

    public static IReadOnlyList<Rule> Rules { get; } =
    [
        new([ConfigKeys.Running.WedgedAfterSeconds, ConfigKeys.Running.HeartbeatSeconds],
            c => I(c, ConfigKeys.Running.WedgedAfterSeconds) >= HeartbeatsBeforeWedged * I(c, ConfigKeys.Running.HeartbeatSeconds),
            c => $"{ConfigKeys.Running.WedgedAfterSeconds.Name} ({I(c, ConfigKeys.Running.WedgedAfterSeconds)}) must be at least {HeartbeatsBeforeWedged} × {ConfigKeys.Running.HeartbeatSeconds.Name} ({I(c, ConfigKeys.Running.HeartbeatSeconds)}): one late heartbeat is not a wedged run"),
        new([ConfigKeys.Requests.MaxRead, ConfigKeys.Requests.MaxQueued],
            c => I(c, ConfigKeys.Requests.MaxRead) >= I(c, ConfigKeys.Requests.MaxQueued),
            c => $"{ConfigKeys.Requests.MaxRead.Name} ({I(c, ConfigKeys.Requests.MaxRead)}) must be at least {ConfigKeys.Requests.MaxQueued.Name} ({I(c, ConfigKeys.Requests.MaxQueued)}): a reader must see every queued request"),
        new([ConfigKeys.Act.MaxListBytes, ConfigKeys.Act.MaxShownNames],
            c => I(c, ConfigKeys.Act.MaxListBytes) >= (long)BytesPerShownName * I(c, ConfigKeys.Act.MaxShownNames),
            c => $"{ConfigKeys.Act.MaxListBytes.Name} ({I(c, ConfigKeys.Act.MaxListBytes)}) must hold {ConfigKeys.Act.MaxShownNames.Name} ({I(c, ConfigKeys.Act.MaxShownNames)}) names of {BytesPerShownName} bytes"),
        new([ConfigKeys.Requests.MaxBytes, ConfigKeys.Act.MaxShownNames],
            c => I(c, ConfigKeys.Requests.MaxBytes) >= ((long)BytesPerShownName * I(c, ConfigKeys.Act.MaxShownNames)) + RequestOverheadBytes,
            c => $"{ConfigKeys.Requests.MaxBytes.Name} ({I(c, ConfigKeys.Requests.MaxBytes)}) must hold a request of {ConfigKeys.Act.MaxShownNames.Name} ({I(c, ConfigKeys.Act.MaxShownNames)}) names of {BytesPerShownName} bytes and {RequestOverheadBytes} more"),
        new([ConfigKeys.Logs.MaxRangeDays, ConfigKeys.Runs.HistoryRetentionDays],
            c => I(c, ConfigKeys.Logs.MaxRangeDays) >= I(c, ConfigKeys.Runs.HistoryRetentionDays),
            c => $"{ConfigKeys.Logs.MaxRangeDays.Name} ({I(c, ConfigKeys.Logs.MaxRangeDays)}) must be at least {ConfigKeys.Runs.HistoryRetentionDays.Name} ({I(c, ConfigKeys.Runs.HistoryRetentionDays)}): every kept day can be asked for"),
        new([ConfigKeys.Records.MaxHistoryBytes, ConfigKeys.Runs.HistoryRetentionDays],
            c => I(c, ConfigKeys.Records.MaxHistoryBytes) >= (long)HistoryBytesPerDay * I(c, ConfigKeys.Runs.HistoryRetentionDays),
            c => $"{ConfigKeys.Records.MaxHistoryBytes.Name} ({I(c, ConfigKeys.Records.MaxHistoryBytes)}) must hold {ConfigKeys.Runs.HistoryRetentionDays.Name} ({I(c, ConfigKeys.Runs.HistoryRetentionDays)}) days of {HistoryBytesPerDay} bytes: a history past its read cap is unreadable"),
        new([ConfigKeys.Systemd.UnitStopTimeoutSeconds, ConfigKeys.Units.StopTimeoutSeconds],
            c => I(c, ConfigKeys.Systemd.UnitStopTimeoutSeconds) >= I(c, ConfigKeys.Units.StopTimeoutSeconds) + StopCeilingMarginSeconds,
            c => $"{ConfigKeys.Systemd.UnitStopTimeoutSeconds.Name} ({I(c, ConfigKeys.Systemd.UnitStopTimeoutSeconds)}) must be at least {ConfigKeys.Units.StopTimeoutSeconds.Name} ({I(c, ConfigKeys.Units.StopTimeoutSeconds)}) + {StopCeilingMarginSeconds}: systemctl stop waits for the unit's own stop"),
        new([ConfigKeys.FileLocks.LockJitterMaxMilliseconds, ConfigKeys.FileLocks.LockJitterMinMilliseconds],
            c => I(c, ConfigKeys.FileLocks.LockJitterMaxMilliseconds) > I(c, ConfigKeys.FileLocks.LockJitterMinMilliseconds),
            c => $"{ConfigKeys.FileLocks.LockJitterMaxMilliseconds.Name} must be above {ConfigKeys.FileLocks.LockJitterMinMilliseconds.Name}"),
        new([ConfigKeys.Timer.PeriodHours],
            c => HoursPerDay % I(c, ConfigKeys.Timer.PeriodHours) == 0,
            c => $"{ConfigKeys.Timer.PeriodHours.Name} ({I(c, ConfigKeys.Timer.PeriodHours)}) must divide the {HoursPerDay} hours of a day: the timer's calendar (00/<hours>) restarts at midnight, so any other period would leave one short interval a day"),
        new([ConfigKeys.Timer.LateSlackMinutes, ConfigKeys.Timer.RandomizedDelayMinutes, ConfigKeys.Timer.AccuracyMinutes],
            c => I(c, ConfigKeys.Timer.LateSlackMinutes) > I(c, ConfigKeys.Timer.RandomizedDelayMinutes) + I(c, ConfigKeys.Timer.AccuracyMinutes),
            c => $"{ConfigKeys.Timer.LateSlackMinutes.Name} ({I(c, ConfigKeys.Timer.LateSlackMinutes)}) must be above {ConfigKeys.Timer.RandomizedDelayMinutes.Name} + {ConfigKeys.Timer.AccuracyMinutes.Name} ({I(c, ConfigKeys.Timer.RandomizedDelayMinutes) + I(c, ConfigKeys.Timer.AccuracyMinutes)}): a run the timer itself delayed is not late"),
        new([ConfigKeys.AgentCpu.MaxBytes, ConfigKeys.AgentCpu.MaxEntries],
            c => I(c, ConfigKeys.AgentCpu.MaxBytes) >= (long)BytesPerCpuEntry * I(c, ConfigKeys.AgentCpu.MaxEntries),
            c => $"{ConfigKeys.AgentCpu.MaxBytes.Name} ({I(c, ConfigKeys.AgentCpu.MaxBytes)}) must hold {ConfigKeys.AgentCpu.MaxEntries.Name} ({I(c, ConfigKeys.AgentCpu.MaxEntries)}) entries of {BytesPerCpuEntry} bytes: a full history past its read cap is no history"),
        new([ConfigKeys.Events.SegmentMinutes, ConfigKeys.Events.SegmentSlackSeconds, ConfigKeys.Commands.MaxTimeoutHours],
            c => (I(c, ConfigKeys.Events.SegmentMinutes) * SecondsPerMinute) + I(c, ConfigKeys.Events.SegmentSlackSeconds) + CeilingMarginSeconds <= I(c, ConfigKeys.Commands.MaxTimeoutHours) * SecondsPerHour,
            c => $"{ConfigKeys.Events.SegmentMinutes.Name} ({I(c, ConfigKeys.Events.SegmentMinutes)}) with {ConfigKeys.Events.SegmentSlackSeconds.Name} ({I(c, ConfigKeys.Events.SegmentSlackSeconds)}) and {CeilingMarginSeconds} s must fit under {ConfigKeys.Commands.MaxTimeoutHours.Name} ({I(c, ConfigKeys.Commands.MaxTimeoutHours)}): the event stream's ceiling is a command's"),
        new([ConfigKeys.Events.RetryMaxSeconds, ConfigKeys.Events.RetryFirstSeconds],
            c => I(c, ConfigKeys.Events.RetryMaxSeconds) >= I(c, ConfigKeys.Events.RetryFirstSeconds),
            c => $"{ConfigKeys.Events.RetryMaxSeconds.Name} must be at least {ConfigKeys.Events.RetryFirstSeconds.Name}"),
        new([ConfigKeys.Timer.RunLimitMinutes, .. RunBudget.Keys],
            c => TimeSpan.FromMinutes(I(c, ConfigKeys.Timer.RunLimitMinutes)) >= RunBudget.TimerRunWorstCase(c),
            c => $"{ConfigKeys.Timer.RunLimitMinutes.Name} ({I(c, ConfigKeys.Timer.RunLimitMinutes)}) must be at least the derived worst case of a timer run ({Math.Ceiling(RunBudget.TimerRunWorstCase(c).TotalMinutes)} min: every command template once at its ceiling with its drains, the two walks, {RunMarginMinutes} min more)"),
        // Plan E14 S2b: the watch samples more often than the interval maximum, or no sample finds a baseline and A19 sees no busy server.
        new([ConfigKeys.McpWatchdog.PeriodMinutes, ConfigKeys.McpServers.CpuIntervalMaxMinutes],
            c => I(c, ConfigKeys.McpWatchdog.PeriodMinutes) < I(c, ConfigKeys.McpServers.CpuIntervalMaxMinutes),
            c => $"{ConfigKeys.McpWatchdog.PeriodMinutes.Name} ({I(c, ConfigKeys.McpWatchdog.PeriodMinutes)}) must be under {ConfigKeys.McpServers.CpuIntervalMaxMinutes.Name} ({I(c, ConfigKeys.McpServers.CpuIntervalMaxMinutes)}): a watch sample finds a CPU baseline only within that maximum, so a longer period would leave A19 no busy evidence"),
        new([ConfigKeys.McpWatchdog.RunLimitMinutes, .. RunBudget.WatchKeys],
            c => TimeSpan.FromMinutes(I(c, ConfigKeys.McpWatchdog.RunLimitMinutes)) >= RunBudget.WatchRunWorstCase(c),
            c => $"{ConfigKeys.McpWatchdog.RunLimitMinutes.Name} ({I(c, ConfigKeys.McpWatchdog.RunLimitMinutes)}) must be at least the derived worst case of a watch run ({Math.Ceiling(RunBudget.WatchRunWorstCase(c).TotalMinutes)} min: the CPU window, every server's log listing, A19's signal grace, {CeilingMarginSeconds} s more)"),
        new([ConfigKeys.Running.NoProgressMinutes, .. RunBudget.Keys],
            c => TimeSpan.FromMinutes(I(c, ConfigKeys.Running.NoProgressMinutes)) >= RunBudget.LongestStep(c),
            c => $"{ConfigKeys.Running.NoProgressMinutes.Name} ({I(c, ConfigKeys.Running.NoProgressMinutes)}) must be at least the longest single command ({Math.Ceiling(RunBudget.LongestStep(c).TotalMinutes)} min with its drains and {CeilingMarginSeconds} s more): a command still inside its ceiling is progress"),
    ];

    /// <summary>Every rule <paramref name="config"/> breaks.</summary>
    public static IReadOnlyList<Rule> Broken(EffectiveConfig config) => [.. Rules.Where(r => !r.Holds(config))];
}
