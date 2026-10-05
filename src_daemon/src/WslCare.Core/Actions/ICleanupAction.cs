using System.Text.Json.Serialization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
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
/// <remarks>The init-only members default to the SAFE answer — no process table, a signal sender that refuses, no shown
/// list — so a context a caller builds without them can only make an action do LESS (E3.S2).</remarks>
public sealed record ActionContext(
    IHostPaths Paths,
    IFileSystem Files,
    TimeProvider Clock,
    EffectiveConfig Config,
    RunTrigger Trigger,
    TargetUserResult TargetUser)
{
    /// <summary>Who must own a file this action reads in the home it works in (plan §15q R1.1): the target user when root works
    /// for them, this process otherwise (<see cref="RegularFiles.HomeFileOwner"/>).</summary>
    public uint HomeFileOwner => TargetUser is TargetUserResult.Found found ? RegularFiles.HomeFileOwner(found.User.Uid) : RegularFiles.EffectiveUid();

    /// <summary>The distro's process table, read fresh at each call (A11's suspects, A12 / A14's "in use"). Unavailable by
    /// default, which makes those actions refuse.</summary>
    public Func<CancellationToken, Reading<ProcessSnapshot>> Processes { get; init; } =
        static _ => Reading.Missing<ProcessSnapshot>("this run has no process table");

    /// <summary>The ONE way a process is signalled (A11): by pid AND start time, never by name. Refuses by default.</summary>
    public IProcessSignals Signals { get; init; } = RefusingProcessSignals.NotWired;

    /// <summary>The volumes a button SHOWED and the person confirmed (plan §15 #4: A4 removes only those, re-checked);
    /// <see cref="ShownList.None"/> for the timer and the terminal, which act on their own fresh preview.</summary>
    public ShownList ShownVolumes { get; init; } = ShownList.None;

    /// <summary>A wait the action may take (A11's CPU window). Real time by default; a test passes its own.</summary>
    public Func<TimeSpan, CancellationToken, Task> Wait { get; init; } = static (delay, token) => Task.Delay(delay, token);

    /// <summary>Whether an EARLIER action of this same run ran and succeeded (A2 runs "after A1", plan §5). Nothing ran by
    /// default — a context built outside the engine never claims a predecessor (E3.S3).</summary>
    public Func<ActionId, bool> RanEarlier { get; init; } = static _ => false;
}

/// <summary>A list of names a caller showed and confirmed — or none given: a closed choice, never a null.</summary>
public sealed record ShownList(bool Given, IReadOnlySet<string> Names)
{
    /// <summary>The most names one shown list carries — in a preview's <c>shown</c> (§15j B1) and back through <c>--volume</c> /
    /// <c>--only</c>: far above the 387 volumes of 2026-10-02, low enough that a mistaken file cannot make a run of millions
    /// (≈ 650 KB of names).</summary>
    public const int MaxNames = 10_000;

    public static readonly ShownList None = new(false, new HashSet<string>(StringComparer.Ordinal));

    /// <summary>Whether a selection of <paramref name="count"/> names is more than one shown list carries (coai E6 plan round #11).</summary>
    public static bool Truncates(int count) => count > MaxNames;

    public static ShownList Of(IEnumerable<string> names) => new(true, new HashSet<string>(names, StringComparer.Ordinal));
}

/// <summary>One object a preview shows or a run removed.</summary>
/// <param name="Kind">volume, container, image, file, …</param>
/// <param name="Name">Its id or name.</param>
/// <param name="Bytes">Its size, when known.</param>
public sealed record ActionItem(string Kind, string Name, long? Bytes, string Note = "")
{
    /// <summary>What the action needs to act on this object again (A11: the pid's start and CPU ticks; A12 / A14: the
    /// folder as this process sees it) — held in memory between the preview and the run of ONE engine call, never
    /// written to a file and never read back from one.</summary>
    [JsonIgnore]
    public string Key { get; init; } = string.Empty;
}

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

    /// <summary>EVERY object the preview selected — <see cref="Items"/> is the first <see cref="MaxItems"/> of them. What the
    /// run acts on, re-checked by it; held in memory within ONE engine call and never serialised (E3.S2).</summary>
    [JsonIgnore]
    public IReadOnlyList<ActionItem> Targets { get; init; } = [];

    /// <summary>Why the action has nothing it COULD do here — its tool is not installed (A8, A17), nothing of its kind
    /// exists — a skip with the reason, never an error (E3.S2); empty when it applies.</summary>
    public string Skip { get; init; } = string.Empty;

    /// <summary>An EVENT that will not wait for an idle machine (plan §5: the event-driven A2 for an order-7 shortage runs at
    /// once) — when set, the engine's idle gate is not asked; every other gate still is (E3.S3). Empty for an ordinary preview.</summary>
    public string Urgent { get; init; } = string.Empty;

    public static ActionPreview Unavailable(string what, string reason) =>
        new(what, false, reason, 0, null, string.Empty, new Dictionary<string, long>(), string.Empty, []);

    /// <summary>An available preview of <paramref name="targets"/> — all kept as <see cref="Targets"/>, the first
    /// <see cref="MaxItems"/> shown as <see cref="Items"/>.</summary>
    public static ActionPreview Of(string what, int count, long? bytes, string basis, IReadOnlyDictionary<string, long> facts, string refusal, IReadOnlyList<ActionItem> targets) =>
        new(what, true, null, count, bytes, basis, facts, refusal, [.. targets.Take(MaxItems)]) { Targets = targets };
}

/// <summary>A folder under the TARGET user's home that an action cleans (plan §15q R2.1, review M8), spelt as segments under
/// the home — what a manual AI agent's data folder may neither be, sit inside, nor contain.</summary>
public sealed record HomeFolder(IReadOnlyList<string> Segments)
{
    public static HomeFolder Of(params string[] segments) => new(segments);

    /// <summary>As a person reads it: <c>~/.cache/ms-playwright</c>.</summary>
    public string Display => "~/" + string.Join('/', Segments);

    /// <summary>The folder under <paramref name="home"/>.</summary>
    public string Under(string home, PathRules rules) => rules.Join(home, [.. Segments]);

    public bool Equals(HomeFolder? other) => other is not null && Segments.SequenceEqual(other.Segments, StringComparer.Ordinal);

    public override int GetHashCode() => Segments.Count;
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

    /// <summary>The targets it did NOT remove, each with why — already gone (not a failure, plan §15a #0), in use since the
    /// preview (Docker's own refusal), refused by the deletion policy, not the same process any more (E3.S2).</summary>
    public IReadOnlyList<ActionItem> NotRemoved { get; init; } = [];

    /// <summary>What else the run detail should say: a part skipped because its tool is not installed, a cross-check.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];

    /// <summary>The run was CUT OFF by a cancellation (a signal) and stopped where it was: what it confirmed before is in
    /// <see cref="Removed"/>, the command in flight and the rest in <see cref="NotRemoved"/> (E6.S0 review D2). The engine
    /// records it as <c>interrupted</c> and stops the run there. Never serialised as false (absent).</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Interrupted { get; init; }

    /// <summary>A run that did nothing because nothing was selected — no command started.</summary>
    public static ActionRun Nothing(IReadOnlyList<ActionCommandRecord> commands, string why = "nothing to remove") =>
        new(0, 0, why, null, null, [], commands, string.Empty);
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

    /// <summary>The folders under the target user's home it cleans — by deleting through the policy OR by a command (npm, pnpm,
    /// pip, uv, dotnet) the deletion policy cannot see into (plan §15q R2.1, review M8). None for an action that touches no
    /// home folder; <c>ActionHomeRootsTests</c> holds every user-scoped action to its declaration.</summary>
    IReadOnlyList<HomeFolder> HomeRoots => [];

    Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken);

    /// <summary>Whether the TIMER's trigger fired for this preview (a button runs regardless, plan §5).</summary>
    TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config);

    Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken);
}

/// <summary>
/// An action whose button run is BOUND to the list its preview showed (plan §15 #4, §15f #11: A4 alone — A5 / A6 / A7
/// re-select live). Its <c>act --preview</c> answer carries <see cref="Shown"/> — every name the preview selected — so the
/// panel can send exactly those back (§15j B1), never the first 20 the items hold.
/// </summary>
public interface IBoundToShownList
{
    /// <summary>Every name <paramref name="preview"/> selected, in its order, at most <see cref="ShownList.MaxNames"/> — the
    /// names a run of this action accepts as its shown list.</summary>
    IReadOnlyList<string> Shown(ActionPreview preview);
}
