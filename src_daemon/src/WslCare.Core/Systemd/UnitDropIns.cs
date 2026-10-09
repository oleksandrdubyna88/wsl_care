using System.Globalization;

using WslCare.Core.Config;
using WslCare.Core.Hosting;

namespace WslCare.Core.Systemd;

/// <summary>
/// E7.S2c: the unit values that are configuration — the timer's period (<c>timer.periodHours</c>, the ONE key every copy of
/// the period derives from), its randomized delay and accuracy, the services' <c>Nice</c>, <c>MemoryMax</c> and
/// <c>TimeoutStopSec</c>, the follower's <c>RestartSec</c> — rendered as one drop-in per unit from the machine layer. The
/// shipped unit files keep today's values (what the drop-in of the defaults says too, a test); <c>install.sh</c> writes
/// <see cref="FileName"/> into <c>&lt;unit&gt;.d/</c> from <c>wsl-care units dropin &lt;unit&gt;</c>, and <c>doctor</c> names a
/// drop-in that no longer matches the configuration (<see cref="Check"/>).
/// </summary>
public static class UnitDropIns
{
    public const string FileName = "50-wsl-care-config.conf";

    public const string Timer = "wsl-care.timer";
    public const string Service = "wsl-care.service";
    public const string Act = "wsl-care-act@.service";
    public const string Events = "wsl-care-events.service";

    /// <summary>The watch (plan E14 S2b): its service and its timer.</summary>
    public const string Watch = "wsl-care-watch.service";

    public const string WatchTimer = "wsl-care-watch.timer";

    /// <summary>Every unit that has a drop-in, in the order <c>install.sh</c> lists its units.</summary>
    public static IReadOnlyList<string> Units { get; } = [Service, Timer, Events, Act, Watch, WatchTimer];

    private const string Header =
        "# Written by install.sh from the machine configuration (wsl-care units dropin {0}). Do not edit: change\n" +
        "# /etc/wsl-care/config.json and run install.sh again; wsl-care doctor names a drop-in that no longer matches.\n";

    /// <summary>The drop-in of <paramref name="unit"/> under the configuration in force (<see cref="Tuning.Current"/>).</summary>
    public static string Render(string unit) => Header.Replace("{0}", unit, StringComparison.Ordinal) + Body(unit);

    /// <summary>Where <paramref name="unit"/>'s drop-in is installed.</summary>
    public static string Path(LinuxHostPaths paths, string unit) => paths.Rules.Join(paths.SystemdUnitDirectory, unit + ".d", FileName);

    private static string Body(string unit) => unit switch
    {
        Timer => Lines(
            "[Timer]",
            // A drop-in ADDS a calendar to the unit's own: the empty assignment clears it first (systemd.timer(5)).
            "OnCalendar=",
            $"OnCalendar={Calendar(Tuning.Current.Int(ConfigKeys.Timer.PeriodHours))}",
            $"RandomizedDelaySec={Text(ConfigKeys.Timer.RandomizedDelayMinutes)}min",
            $"AccuracySec={Text(ConfigKeys.Timer.AccuracyMinutes)}min"),
        // The timer's run is ended as a whole at timer.runLimitMinutes (review C-H2, above its derived worst case); a detached
        // confirm never is (TimeoutStartSec=infinity stays in its unit file) — the progress watchdog stands for both.
        Service => Lines(
            "[Service]",
            $"Nice={Text(ConfigKeys.Units.Nice)}",
            $"MemoryMax={Text(ConfigKeys.Units.MemoryMaxMb)}M",
            $"TimeoutStopSec={Text(ConfigKeys.Units.StopTimeoutSeconds)}",
            $"TimeoutStartSec={Text(ConfigKeys.Timer.RunLimitMinutes)}min"),
        Act => Lines(
            "[Service]",
            $"Nice={Text(ConfigKeys.Units.Nice)}",
            $"MemoryMax={Text(ConfigKeys.Units.MemoryMaxMb)}M",
            $"TimeoutStopSec={Text(ConfigKeys.Units.StopTimeoutSeconds)}"),
        Events => Lines("[Service]", $"RestartSec={Text(ConfigKeys.Units.EventsRestartSeconds)}", $"MemoryMax={Text(ConfigKeys.Units.MemoryMaxMb)}M"),
        // Plan E14 S2b: the watch is a root run with the full run's hardening values, ended as a whole at mcpWatchdog.runLimitMinutes.
        Watch => Lines(
            "[Service]",
            $"Nice={Text(ConfigKeys.Units.Nice)}",
            $"MemoryMax={Text(ConfigKeys.Units.MemoryMaxMb)}M",
            $"TimeoutStopSec={Text(ConfigKeys.Units.StopTimeoutSeconds)}",
            $"TimeoutStartSec={Text(ConfigKeys.McpWatchdog.RunLimitMinutes)}min"),
        // The two monotonic times are lists too: the empty assignment clears the unit's own first (systemd.timer(5)).
        WatchTimer => Lines(
            "[Timer]",
            "OnBootSec=",
            $"OnBootSec={Text(ConfigKeys.McpWatchdog.PeriodMinutes)}min",
            "OnUnitActiveSec=",
            $"OnUnitActiveSec={Text(ConfigKeys.McpWatchdog.PeriodMinutes)}min"),
        _ => throw new ArgumentOutOfRangeException(nameof(unit), unit, $"a unit without a drop-in; one of: {string.Join(", ", Units)}"),
    };

    /// <summary>Whether the installed drop-in of <paramref name="unit"/> says what the configuration says: empty when it does,
    /// or when there is none and the configuration keeps the unit's own values; else what differs, in words.</summary>
    /// <param name="installed">The installed file's text; empty when there is none.</param>
    public static string Check(string unit, string installed)
    {
        var wanted = Render(unit);
        return installed.Length == 0
            ? (wanted == Defaults(unit) ? string.Empty : $"{unit} has no {FileName}, and the machine configuration changes its values: run install.sh again")
            : Normalised(installed) == Normalised(wanted) ? string.Empty : $"{unit}'s {FileName} does not match the machine configuration ({Differences(installed, wanted)}): run install.sh again";
    }

    /// <summary>The drop-in of the embedded defaults — what the shipped unit file itself says.</summary>
    public static string Defaults(string unit)
    {
        using (Tuning.Use(Tuning.Default.Config))
        {
            return Render(unit);
        }
    }

    private static string Differences(string installed, string wanted)
    {
        var have = Settings(installed);
        return string.Join("; ", Settings(wanted).Where(w => !have.Contains(w)).Select(w => $"wants {w}"));
    }

    private static HashSet<string> Settings(string text) =>
        [.. Normalised(text).Split('\n').Where(l => l.Contains('=', StringComparison.Ordinal) && !l.EndsWith('=')).Select(l => l.Trim())];

    private static string Normalised(string text) => string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0 && !l.StartsWith('#')));

    /// <summary>The timer's calendar for a period that divides the day: every <paramref name="hours"/> from midnight
    /// (<c>00/&lt;hours&gt;</c>) — and once a day, midnight itself, for 24. Retro review of PR #8, O1: systemd 255 refuses
    /// <c>*-*-* 00/24:00:00</c> ("Invalid argument"), so a timer given it never fired.</summary>
    private static string Calendar(int hours) =>
        hours == HoursPerDay ? "*-*-* 00:00:00" : string.Create(CultureInfo.InvariantCulture, $"*-*-* 00/{hours}:00:00");

    private const long HoursPerDay = TimeSpan.TicksPerDay / TimeSpan.TicksPerHour;

    private static string Text(ConfigKey.IntKey key) => Tuning.Current.Int(key).ToString(CultureInfo.InvariantCulture);

    private static string Lines(params string[] lines) => string.Join('\n', lines) + "\n";
}
