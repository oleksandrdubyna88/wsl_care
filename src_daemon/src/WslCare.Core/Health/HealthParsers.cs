using System.Globalization;
using System.Text.Json;

using WslCare.Core.Collectors;
using WslCare.Core.Collectors.Procfs;
using WslCare.Core.Processes;
using WslCare.Core.Systemd;

namespace WslCare.Core.Health;

/// <summary>One failed unit as <c>systemctl list-units --failed --output=json</c> names it.</summary>
public sealed record FailedUnit(string Unit, string Description);

/// <summary>What <c>timedatectl show</c> says about time keeping: whether NTP is on and whether the clock is synchronised.</summary>
public sealed record TimeSync(bool Ntp, bool Synchronized);

/// <summary>The three lines the clock probe prints on the Windows side (<see cref="HealthCommands.WindowsClock"/>).</summary>
/// <param name="PrintedAt">Windows' clock when the script ran, UTC.</param>
/// <param name="ProcessStartedAt">Windows' clock when the PowerShell process started, UTC — the same clock, so
/// <c>PrintedAt − ProcessStartedAt</c> is the launch latency, measured without any skew in it.</param>
/// <param name="Profile">The Windows user profile (<c>C:\Users\…</c>).</param>
public sealed record WindowsClockAnswer(DateTimeOffset PrintedAt, DateTimeOffset ProcessStartedAt, string Profile)
{
    /// <summary>The Windows Time service from the probe's tagged lines (PLAN_windows_time_guard.md D1), or why not.</summary>
    public Reading<WindowsTimeService> TimeService { get; init; } = Reading.Missing<WindowsTimeService>("the Windows clock probe printed no w32time line");
}

/// <summary>A snap revision <c>snap list --all</c> marks <c>disabled</c>: superseded, kept by snapd, what A9 removes.</summary>
public sealed record SnapRevision(string Name, string Revision);

/// <summary>The keys of <c>.wslconfig</c> the audit reads (plan §4.5), as written; empty when absent.</summary>
public sealed record WslConfigSettings(string Memory, string Swap, string AutoMemoryReclaim, string SparseVhd);

/// <summary>The parsers of the health collectors — pure, so each shape is a unit test over captured output.</summary>
public static class HealthParsers
{
    /// <summary>The JSON array of <c>systemctl list-units --failed --output=json</c> (systemd 255, measured 2026-10-02:
    /// <c>[{"unit":"getty@tty1.service","load":"loaded","active":"failed","sub":"failed","description":"…"}]</c>).</summary>
    public static Reading<IReadOnlyList<FailedUnit>> FailedUnits(string stdout) =>
        JsonArray(stdout, "systemctl list-units --failed", row => new FailedUnit(Text(row, "unit"), Text(row, "description")));

    /// <summary>The oldest entry the journal holds: the smallest <c>first_entry</c> (microseconds since the epoch) of
    /// <c>journalctl --list-boots --output=json</c>.</summary>
    public static Reading<DateTimeOffset> OldestJournalEntry(string stdout) =>
        JsonArray(stdout, "journalctl --list-boots", row => row.TryGetProperty("first_entry", out var f) && f.TryGetInt64(out var micros) ? micros : 0L)
            .Bind(entries => entries.Where(e => e > 0).DefaultIfEmpty(0).Min() is var oldest and > 0
                ? Reading.Of(DateTimeOffset.UnixEpoch.AddTicks(oldest * 10))
                : Reading.Missing<DateTimeOffset>("journalctl --list-boots listed no boot with a first entry"));

    /// <summary>
    /// The lines a journal search matched. journalctl exits 1 printing nothing when NOTHING matched (measured
    /// 2026-10-02, systemd 255) — that is a count of 0, not a failure; any other non-zero exit, or one that printed
    /// on stderr, is unavailable with the reason.
    /// </summary>
    public static Reading<IReadOnlyList<string>> SearchMatches(ToolCommand command, CommandOutcome outcome) => outcome switch
    {
        CommandOutcome.Exited { ExitCode: 1 } e when e.Stdout.Text.Trim().Length == 0 && e.Stderr.Text.Trim().Length == 0 => Reading.Of<IReadOnlyList<string>>([]),
        _ => ToolAnswers.Read(command, outcome).Map<IReadOnlyList<string>>(stdout => [.. ProcText.Lines(stdout)]),
    };

    /// <summary><c>timedatectl show --property=NTP --property=NTPSynchronized</c>: <c>NTP=yes</c>, <c>NTPSynchronized=yes</c>.</summary>
    public static Reading<TimeSync> TimeSync(string stdout)
    {
        var values = SystemdText.KeyValues(stdout);
        return values.TryGetValue("NTP", out var ntp) && values.TryGetValue("NTPSynchronized", out var synced)
            ? Reading.Of(new TimeSync(ntp == "yes", synced == "yes"))
            : Reading.Missing<TimeSync>($"timedatectl show printed no NTP / NTPSynchronized line: \"{stdout.Trim()}\"");
    }

    /// <summary>The first line of <c>systemctl --version</c> (<c>systemd 255 (255.4-1ubuntu8.17)</c>).</summary>
    public static Reading<string> SystemdVersion(string stdout) =>
        ProcText.Lines(stdout).FirstOrDefault() is { } first ? Reading.Of(first.Trim()) : Reading.Missing<string>("systemctl --version printed nothing");

    /// <summary>Whether <c>/</c> is mounted with <c>discard</c> (plan §4.5: if not, <c>fstrim.timer</c> must run).</summary>
    public static Reading<bool> RootHasDiscard(string procMounts)
    {
        var root = ProcText.Lines(procMounts).Select(l => l.Split(' ')).LastOrDefault(f => f.Length >= 4 && f[1] == "/");
        return root is null
            ? Reading.Missing<bool>("/proc/mounts has no line for /")
            : Reading.Of(root[3].Split(',').Contains("discard", StringComparer.Ordinal));
    }

    /// <summary><c>/proc/uptime</c>'s first number: seconds since boot.</summary>
    public static Reading<TimeSpan> Uptime(string procUptime) =>
        double.TryParse(procUptime.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? Reading.Of(TimeSpan.FromSeconds(seconds))
            : Reading.Missing<TimeSpan>($"/proc/uptime is not a number: \"{procUptime.Trim()}\"");

    /// <summary><c>/etc/wsl.conf</c>'s <c>[automount] root</c> — where the Windows drives appear; <c>/mnt/</c> when unset.</summary>
    public static string AutomountRoot(string wslConf)
    {
        var root = Ini(wslConf).GetValueOrDefault("automount.root", string.Empty).Trim().Trim('"');
        return root.Length == 0 ? "/mnt/" : root.EndsWith('/') ? root : root + "/";
    }

    /// <summary><c>.wslconfig</c>: <c>[wsl2] memory</c>, <c>swap</c>, and <c>autoMemoryReclaim</c> / <c>sparseVhd</c>
    /// in either <c>[wsl2]</c> or <c>[experimental]</c> (WSL moved them between versions).</summary>
    public static WslConfigSettings WslConfig(string text)
    {
        var ini = Ini(text);
        string Either(string key) => ini.GetValueOrDefault("wsl2." + key) ?? ini.GetValueOrDefault("experimental." + key) ?? string.Empty;
        return new(Either("memory"), Either("swap"), Either("automemoryreclaim"), Either("sparsevhd"));
    }

    /// <summary>The clock probe's three positional lines — unavailable when any is missing or not an instant — and, since
    /// PLAN_windows_time_guard.md D1, its TAGGED lines, read by tag and never counted as a position (an empty profile must not
    /// read a tag as the profile).</summary>
    public static Reading<WindowsClockAnswer> WindowsClock(string stdout)
    {
        var all = ProcText.Lines(stdout).Select(l => l.Trim()).ToList();
        var lines = all.Where(l => !IsTagged(l)).ToList();
        return lines.Count >= 3 && Instant(lines[0]) is { } printed && Instant(lines[1]) is { } started
            ? Reading.Of(new WindowsClockAnswer(printed, started, lines[2]) { TimeService = ClockParsers.TimeService(all) })
            : Reading.Missing<WindowsClockAnswer>($"the Windows clock probe did not print two instants and a profile: \"{stdout.Trim()}\"");
    }

    private static bool IsTagged(string line) =>
        line.StartsWith(HealthCommands.TimeServiceStatusTag, StringComparison.Ordinal) || line.StartsWith(HealthCommands.TimeServiceStartTypeTag, StringComparison.Ordinal);

    /// <summary><c>snap list --all</c>: the rows whose Notes column holds <c>disabled</c> (Name Version Rev Tracking Publisher Notes).</summary>
    public static IReadOnlyList<SnapRevision> DisabledSnapRevisions(string stdout) =>
        [.. ProcText.Lines(stdout).Skip(1)
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(f => f.Length >= 6 && f[^1].Split(',').Contains("disabled", StringComparer.Ordinal))
            .Select(f => new SnapRevision(f[0], f[2]))];

    private static DateTimeOffset? Instant(string text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    /// <summary>An INI file as <c>section.key</c> (lower case) → value; comments (<c>#</c>, <c>;</c>) skipped.</summary>
    private static Dictionary<string, string> Ini(string text)
    {
        var section = string.Empty;
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in ProcText.Lines(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1].Trim().ToLowerInvariant();
            }
            else if (line.IndexOf('=', StringComparison.Ordinal) is var eq and > 0)
            {
                values[$"{section}.{line[..eq].Trim().ToLowerInvariant()}"] = line[(eq + 1)..].Trim();
            }
        }

        return values;
    }

    private static Reading<IReadOnlyList<T>> JsonArray<T>(string stdout, string what, Func<JsonElement, T> row)
    {
        try
        {
            using var document = JsonDocument.Parse(stdout);
            return document.RootElement.ValueKind == JsonValueKind.Array
                ? Reading.Of<IReadOnlyList<T>>([.. document.RootElement.EnumerateArray().Select(row)])
                : Reading.Missing<IReadOnlyList<T>>($"{what} did not print a JSON array");
        }
        catch (JsonException e)
        {
            return Reading.Missing<IReadOnlyList<T>>($"{what} did not print JSON: {e.Message}");
        }
    }

    private static string Text(JsonElement row, string name) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;
}
