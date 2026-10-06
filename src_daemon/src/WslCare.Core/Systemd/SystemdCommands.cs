using WslCare.Core.Config;
using System.Globalization;

using WslCare.Core.Processes;

namespace WslCare.Core.Systemd;

/// <summary>Where a journal search looks: the kernel's messages of this boot, or one unit's.</summary>
public abstract record JournalScope
{
    private JournalScope()
    {
    }

    /// <summary><c>--dmesg</c>: the kernel ring buffer as journald kept it — this boot only (journalctl implies <c>-b</c>).</summary>
    public sealed record Kernel : JournalScope;

    /// <summary><c>--unit=</c>: one unit's messages.</summary>
    public sealed record Unit(string Name) : JournalScope;
}

/// <summary>
/// The <c>systemctl</c>, <c>journalctl</c> and <c>timedatectl</c> commands the health collectors read with (plan
/// §4.5), in ONE place — read-only, each with its ceiling. A test holds every command built here to
/// <see cref="ReadVerbs"/>, as <c>DockerCommands</c> does for Docker.
/// </summary>
public static class SystemdCommands
{
    public const string Systemctl = "systemctl";
    public const string Journalctl = "journalctl";
    public const string Timedatectl = "timedatectl";

    /// <summary>The unit properties <see cref="SystemdUnit"/> reads.</summary>
    public const string UnitProperties = "Id,LoadState,ActiveState,SubState,Result,NRestarts,ActiveEnterTimestamp,UnitFileState";

    private static int Cap => Tuning.Current.Int(ConfigKeys.Systemd.OutputCapBytes);

    private static int SearchCap => Tuning.Current.Int(ConfigKeys.Systemd.SearchOutputCapBytes);

    public static TimeSpan Ceiling => Tuning.Current.Seconds(ConfigKeys.Systemd.TimeoutSeconds);

    /// <summary>A journal search reads every entry since the instant (the clock-jump search reads ~1 700 matching
    /// lines a day, measured 2026-10-02: 871 in 2.6 h of uptime); it gets <c>systemd.searchTimeoutSeconds</c>.</summary>
    public static TimeSpan SearchCeiling => Tuning.Current.Seconds(ConfigKeys.Systemd.SearchTimeoutSeconds);

    /// <summary><c>journalctl --disk-usage</c>: how much the journal holds (plan §4.5, A10's trigger).</summary>
    public static ToolCommand JournalDiskUsage => new(Journalctl, "journalctl-disk-usage", ["--disk-usage"], Ceiling, Cap);

    /// <summary><c>journalctl --list-boots</c> as JSON (systemd ≥ 252): the first entry of the oldest boot is the
    /// oldest entry the journal holds — how long its history is (plan §4.5, F4: clock jumps erase it).</summary>
    public static ToolCommand ListBoots => new(Journalctl, "journalctl-list-boots", ["--list-boots", "--output=json", "--no-pager"], Ceiling, Cap);

    /// <summary><c>systemctl list-units --failed</c> as JSON: the failed units (plan §4.5).</summary>
    public static ToolCommand FailedUnits => new(Systemctl, "systemctl-failed", ["list-units", "--failed", "--output=json", "--no-pager"], Ceiling, Cap);

    /// <summary><c>systemctl --version</c>: the first line names systemd's version (<c>doctor</c>).</summary>
    public static ToolCommand Version => new(Systemctl, "systemctl-version", ["--version"], Ceiling, Cap);

    /// <summary><c>timedatectl show</c> for the two properties the clock check reads: whether NTP is on and whether
    /// the clock is synchronised (plan §15 #10: a synchronised clock is not corrected).</summary>
    public static ToolCommand TimeSync => new(Timedatectl, "timedatectl-show", ["show", "--property=NTP", "--property=NTPSynchronized"], Ceiling, Cap);

    /// <summary>
    /// The leading words of every command built here. Each one only READS; a test holds every command to this
    /// list, and the scenarios hold every argv the fakes saw to it.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<string>> ReadVerbs { get; } =
    [
        ["--disk-usage"], ["--list-boots"], ["list-units"], ["--version"], ["show"], ["--since"],
    ];

    /// <summary>One unit's state, timestamps as <c>@unix-seconds</c> so they parse without a time zone.</summary>
    public static ToolCommand ShowUnit(string unit) =>
        new(Systemctl, "systemctl-show", ["show", unit, "--timestamp=unix", $"--property={UnitProperties}"], Ceiling, Cap);

    /// <summary>
    /// The journal lines since <paramref name="since"/> in <paramref name="scope"/> that match <paramref name="pattern"/>
    /// (a PCRE2 pattern, <c>--grep</c>), message text only, one per line. journalctl exits 1 with nothing printed
    /// when nothing matched — <see cref="JournalMatches.Count"/> reads that as 0.
    /// </summary>
    public static ToolCommand Search(DateTimeOffset since, JournalScope scope, string pattern) =>
        new(Journalctl, "journalctl-search", ["--since", "@" + since.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), "--no-pager", "--quiet", "--output=cat", Scope(scope), "--grep=" + pattern], SearchCeiling, SearchCap);

    /// <summary>Whether an argv (without the executable) starts with one of <see cref="ReadVerbs"/>.</summary>
    public static bool IsReadVerb(IReadOnlyList<string> arguments) =>
        ReadVerbs.Any(verb => verb.Count <= arguments.Count && verb.Zip(arguments).All(p => string.Equals(p.First, p.Second, StringComparison.Ordinal)));

    private static string Scope(JournalScope scope) => scope switch
    {
        JournalScope.Kernel => "--dmesg",
        JournalScope.Unit unit => "--unit=" + unit.Name,
        _ => throw new System.Diagnostics.UnreachableException("JournalScope is a closed set"),
    };
}
