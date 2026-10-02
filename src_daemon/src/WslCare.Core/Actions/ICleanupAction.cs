using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;

namespace WslCare.Core.Actions;

/// <summary>When an action waits for an idle machine (plan §5 <i>Heavy actions wait for idle</i>).</summary>
public enum IdleRule
{
    /// <summary>Never: it is light (A10's vacuum).</summary>
    Never,

    /// <summary>On the timer only (A1, A2): a button press is the owner's decision now.</summary>
    TimerOnly,

    /// <summary>Always (A7, A15): even a button waits — it is refused with the reason while the machine is busy.</summary>
    Always,
}

/// <summary>Everything an action reaches the machine through, for one run — built once by the engine.</summary>
/// <param name="TargetUser">Whose home and tools a user-scoped action works on (plan §15c #2), discovered once per run.</param>
public sealed record ActionContext(
    IHostPaths Paths,
    IFileSystem Files,
    TimeProvider Clock,
    EffectiveConfig Config,
    RunTrigger Trigger,
    TargetUserResult TargetUser);

/// <summary>One object a preview shows or a run removed.</summary>
/// <param name="Kind">volume, container, image, file, …</param>
/// <param name="Name">Its id or name.</param>
/// <param name="Bytes">Its size, when known.</param>
public sealed record ActionItem(string Kind, string Name, long? Bytes, string Note = "");

/// <summary>
/// What an action WOULD do, computed from LIVE state (plan §15a #0) — the same figure the button's modal shows and the
/// timer's dry run records. Unavailable (with the reason) when the state could not be read; never rendered as 0.
/// </summary>
/// <param name="What">The action in words, with the limits in force.</param>
/// <param name="Count">How many objects it would take.</param>
/// <param name="Bytes">How many bytes those objects hold, when known.</param>
/// <param name="Basis">Where the figures come from.</param>
/// <param name="Facts">Figures the trigger reads (the journal's size), by name.</param>
/// <param name="Refusal">Why the action would refuse to run although it could be previewed; empty when it would not.</param>
/// <param name="Items">The objects, the first <see cref="MaxItems"/>.</param>
public sealed record ActionPreview(
    string What,
    bool Available,
    string? Reason,
    int Count,
    long? Bytes,
    string Basis,
    IReadOnlyDictionary<string, long> Facts,
    string Refusal,
    IReadOnlyList<ActionItem> Items)
{
    /// <summary>Plan §7.3: the modal names up to 20 items.</summary>
    public const int MaxItems = 20;

    public static ActionPreview Unavailable(string what, string reason) =>
        new(what, false, reason, 0, null, string.Empty, new Dictionary<string, long>(), string.Empty, []);
}

/// <summary>The timer's trigger: fired, or why not (plan §5's <i>Auto trigger</i> column).</summary>
public sealed record TriggerDecision(bool Fired, string Reason);

/// <summary>One command an action ran, as the run detail keeps it.</summary>
/// <param name="Exit">The exit code; <c>null</c> when it did not exit (refused, failed to start, timed out).</param>
public sealed record ActionCommandRecord(string Template, string Display, string Outcome, int? Exit, string Detail);

/// <summary>
/// What running an action did — MEASURED (plan §5 <i>Freed bytes are measured, not estimated</i>): the objects it
/// removed, the bytes before and after on the basis it names, the commands it ran. <see cref="Failure"/> is empty when
/// it succeeded.
/// </summary>
public sealed record ActionRun(
    int Count,
    long? FreedBytes,
    string FreedBasis,
    long? BeforeBytes,
    long? AfterBytes,
    IReadOnlyList<ActionItem> Removed,
    IReadOnlyList<ActionCommandRecord> Commands,
    string Failure)
{
    public bool Succeeded => Failure.Length == 0;
}

/// <summary>
/// One action of plan §5: <c>ICleanupAction { Id; Preview(record, config); Run(executor) }</c>. Every action declares
/// the argv templates it runs (<see cref="Commands"/>) — the only ones its <see cref="ActionCommands"/> lets it bind, and
/// part of the policy's catalogue — and previews from LIVE state; the engine decides whether it runs.
/// </summary>
public interface ICleanupAction
{
    ActionId Id { get; }

    /// <summary>One line: what it does.</summary>
    string Summary { get; }

    /// <summary>Machine-scoped (runs as the daemon) or user-scoped (needs the target user, plan §15c #2).</summary>
    CommandScope Scope { get; }

    IdleRule Idle { get; }

    /// <summary>The sides it runs on; the other binary refuses it with the reason.</summary>
    IReadOnlyList<HostSide> Sides { get; }

    /// <summary>Every argv template it may run — read and write alike.</summary>
    IReadOnlyList<CommandTemplate> Commands { get; }

    Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken);

    /// <summary>Whether the TIMER's trigger fired for this preview (a button runs regardless, plan §5).</summary>
    TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config);

    Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken);
}
