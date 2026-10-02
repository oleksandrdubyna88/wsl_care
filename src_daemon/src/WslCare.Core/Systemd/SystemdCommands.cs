using WslCare.Core.Processes;

namespace WslCare.Core.Systemd;

/// <summary>
/// The <c>systemctl</c> and <c>journalctl</c> commands the health collectors read with (plan §4.5), in
/// ONE place — read-only, each with its ceiling. E2.S2 brings the commands and their parsers, because the
/// live contract (plan §15b #6) holds the parsers to the real tools; the collectors that call them are
/// E2.S3's.
/// </summary>
public static class SystemdCommands
{
    public const string Systemctl = "systemctl";
    public const string Journalctl = "journalctl";

    /// <summary>The unit properties <see cref="SystemdUnit"/> reads.</summary>
    public const string UnitProperties = "Id,LoadState,ActiveState,SubState,Result,NRestarts,ActiveEnterTimestamp";

    private const int Cap = 1024 * 1024;

    public static readonly TimeSpan Ceiling = TimeSpan.FromSeconds(15);

    /// <summary><c>journalctl --disk-usage</c>: how much the journal holds (plan §4.5, A10's trigger).</summary>
    public static ToolCommand JournalDiskUsage { get; } = new(Journalctl, "journalctl-disk-usage", ["--disk-usage"], Ceiling, Cap);

    /// <summary>One unit's state, timestamps as <c>@unix-seconds</c> so they parse without a time zone.</summary>
    public static ToolCommand ShowUnit(string unit) =>
        new(Systemctl, "systemctl-show", ["show", unit, "--timestamp=unix", $"--property={UnitProperties}"], Ceiling, Cap);
}
