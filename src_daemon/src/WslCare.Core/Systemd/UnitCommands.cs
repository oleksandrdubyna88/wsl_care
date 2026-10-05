using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;

namespace WslCare.Core.Systemd;

/// <summary>
/// The systemd commands a DETACHED run needs (plan §15j B2, M4, §15k #2), declared here and nowhere else, each with a
/// CLOSED unit slot (<see cref="SlotKind.ActUnit"/>: only <c>wsl-care-act@&lt;runId&gt;.service</c>; for a stop also
/// <c>wsl-care.service</c>) and its own ceiling: start the template unit for one run without waiting for it, stop a wedged
/// run's unit, and read whether a run's unit is still busy (its active state and any queued job). Never <c>systemd-run</c> —
/// a generic wrapper the never-list forbids.
/// </summary>
public static class UnitCommands
{
    /// <summary>The timer's unit — the only unit besides an act instance a stop may name.</summary>
    public const string TimerService = "wsl-care.service";

    private const int Cap = 64 * 1024;

    /// <summary>A start returns at once (<c>--no-block</c>: the job is queued); a stop waits for the unit, which systemd bounds
    /// with <c>TimeoutStopSec=90</c> — the ceiling stays above it.</summary>
    public static readonly TimeSpan StartCeiling = TimeSpan.FromSeconds(30);

    public static readonly TimeSpan StopCeiling = TimeSpan.FromSeconds(120);

    private static readonly SlotKind.ActUnit Act = new();

    private static readonly SlotKind StoppableUnit = new SlotKind.AnyOf([new SlotKind.OneOf([TimerService]), Act]);

    public static CommandTemplate StartTemplate { get; } = Template("systemctl-start-act", [L("start"), L("--no-block"), S("unit", Act)], StartCeiling);

    public static CommandTemplate StopTemplate { get; } = Template("systemctl-stop", [L("stop"), S("unit", StoppableUnit)], StopCeiling);

    public static CommandTemplate ShowTemplate { get; } = Template("systemctl-show-act", [L("show"), L("--property=ActiveState"), L("--property=Job"), S("unit", Act)], SystemdCommands.Ceiling);

    /// <summary>Every template here — part of the product's catalogue (<see cref="CommandCatalogue.Product"/>).</summary>
    public static IReadOnlyList<CommandTemplate> All { get; } = [StartTemplate, StopTemplate, ShowTemplate];

    public static ToolCommand Start(RunId runId) =>
        new(SystemdCommands.Systemctl, StartTemplate.Name, ["start", "--no-block", SlotKind.ActUnit.Of(runId)], StartCeiling, Cap);

    /// <summary>A stop of <paramref name="unit"/> — the caller has already checked it is one of the two units.</summary>
    public static ToolCommand Stop(string unit) =>
        new(SystemdCommands.Systemctl, StopTemplate.Name, ["stop", unit], StopCeiling, Cap);

    public static ToolCommand Show(RunId runId) =>
        new(SystemdCommands.Systemctl, ShowTemplate.Name, ["show", "--property=ActiveState", "--property=Job", SlotKind.ActUnit.Of(runId)], SystemdCommands.Ceiling, Cap);

    /// <summary>Whether <c>systemctl show</c>'s answer says the unit is still busy: a queued job, or an active / activating /
    /// deactivating / reloading state (plan §15k #2: not only <c>is-active</c> — a start that is queued has no active state yet).</summary>
    public static bool Busy(string showOutput)
    {
        var lines = showOutput.Split('\n').Select(l => l.Trim()).ToList();
        var job = Value(lines, "Job");
        var state = Value(lines, "ActiveState");
        return job is not ("" or "0") || state is "active" or "activating" or "deactivating" or "reloading";
    }

    /// <summary>The unit's active state as <c>systemctl show</c> printed it; empty when it did not.</summary>
    public static string ActiveState(string showOutput) => Value([.. showOutput.Split('\n').Select(l => l.Trim())], "ActiveState");

    private static string Value(IReadOnlyList<string> lines, string key) =>
        lines.FirstOrDefault(l => l.StartsWith(key + "=", StringComparison.Ordinal)) is { } line ? line[(key.Length + 1)..] : string.Empty;

    private static CommandTemplate Template(string name, IReadOnlyList<ArgPart> parts, TimeSpan ceiling) =>
        new(name, CommandScope.Machine, SystemdCommands.Systemctl, parts, ceiling, Cap);

    private static ArgPart.Literal L(string text) => new(text);

    private static ArgPart.Slot S(string name, SlotKind kind) => new(name, kind);
}
