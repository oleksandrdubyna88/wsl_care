using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Health;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Records;
using WslCare.Core.Systemd;

namespace WslCare.Core.Actions.Memory;

/// <summary>
/// A2 (plan §5 as amended by §15c #3): <c>sysctl -w vm.compact_memory=1</c> — defragmentation only. Auto trigger: AFTER A1
/// ran in the same run, or the EVENT of plan §4.1 — no free order-7 block left in zone Normal, or a <c>page allocation
/// failure</c> in the kernel log since the last run (the VMBus signature of 2026-09-09/16). The timer's A2 waits for an
/// idle machine like A1; the EVENT does not wait (plan §5: <i>the event-driven A2 for an order-7 shortage runs at once</i>) —
/// its preview is <see cref="ActionPreview.Urgent"/>, so the engine's idle gate is not asked. Every other gate still is:
/// the <c>auto</c> switch, the dry-run week.
/// </summary>
/// <remarks>
/// <para><b>Where the event comes from.</b> Every run that previews A2 looks: the timer's pass inside <c>collect</c> (every
/// 4 h), a button, and <c>act A2</c> started by any timer (E4.S1's units may schedule it more often — the event is then
/// acted on within that period). There is no watcher process of its own (E3.S3 decision).</para>
/// <para><b>Measured</b>: the free order-7 blocks read from <c>/proc/buddyinfo</c> before and after; the before / after
/// figures are those blocks' bytes. A2 frees no disk and no memory — <see cref="ActionRun.FreedBytes"/> stays unknown.</para>
/// </remarks>
public sealed class Compaction : ICleanupAction
{
    public const string Order7BlocksFact = "order7Blocks";

    public const string AllocationFailuresFact = "allocationFailures";

    public const string AfterA1Fact = "afterA1";

    public static readonly CommandTemplate CompactMemory = new(
        "sysctl-compact-memory",
        CommandScope.Machine,
        "sysctl",
        [new ArgPart.Literal("-w"), new ArgPart.Literal("vm.compact_memory=1")],
        TimeSpan.FromMinutes(2),
        CommandRequest.DefaultOutputCapChars);

    private static readonly ActionId A1 = ActionId.Find("A1")!;

    public ActionId Id { get; } = ActionId.Find("A2")!;

    public string Summary => "sysctl -w vm.compact_memory=1: defragment free memory (after A1, or at once on an order-7 shortage)";

    public CommandScope Scope => CommandScope.Machine;

    public IdleRule Idle => IdleRule.TimerOnly;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [CompactMemory, ReadCommandTemplates.JournalSearch];

    public async Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        const string what = "sysctl -w vm.compact_memory=1: the kernel moves pages so free memory forms large blocks again";
        var memory = MemoryNow.Read(context).Bind(m => m.Fragmentation);
        if (memory is not Reading<Collectors.Procfs.Fragmentation>.Available { Value: var f })
        {
            return ActionPreview.Unavailable(what, memory.ReasonOrEmpty);
        }

        var failures = await AllocationFailuresAsync(context, commands, cancellationToken).ConfigureAwait(false);
        var facts = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [Order7BlocksFact] = f.BlocksOrder7Plus,
            [AfterA1Fact] = context.RanEarlier(A1) ? 1 : 0,
        };
        if (failures is Reading<int>.Available { Value: var count })
        {
            facts[AllocationFailuresFact] = count;
        }

        IReadOnlyList<ActionItem> items = [new ActionItem("free memory", $"zone {f.Zone}", f.BytesOrder7Plus, string.Create(CultureInfo.InvariantCulture, $"{f.BlocksOrder7Plus} free order-7 blocks (512 KiB), {f.BlocksOrder4Plus} order-4 (64 KiB)"))];
        var basis = "/proc/buddyinfo now; the kernel log since the last run" + (failures.IsAvailable ? string.Empty : $" (not read: {failures.ReasonOrEmpty})");
        return ActionPreview.Of(what, 1, null, basis, facts, string.Empty, items) with { Urgent = Event(f.BlocksOrder7Plus, failures) };
    }

    /// <summary>Plan §5: after A1 ran in this run, or the event of plan §4.1.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config)
    {
        if (preview.Urgent.Length > 0)
        {
            return new TriggerDecision(true, preview.Urgent);
        }

        if (preview.Facts.GetValueOrDefault(AfterA1Fact) == 1)
        {
            return new TriggerDecision(true, "A1 ran in this run (plan 5: A2 after A1)");
        }

        return preview.Facts.TryGetValue(Order7BlocksFact, out var blocks)
            ? new TriggerDecision(false, string.Create(CultureInfo.InvariantCulture, $"{blocks} free order-7 blocks, no page allocation failure since the last run, and A1 did not run; the trigger is A1 having run, no order-7 block, or an allocation failure"))
            : new TriggerDecision(false, "the free blocks were not read");
    }

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        var before = MemoryNow.Read(context).Bind(m => m.Fragmentation);
        var outcome = await commands.RunAsync(CompactMemory, [], cancellationToken).ConfigureAwait(false);
        var after = MemoryNow.Read(context).Bind(m => m.Fragmentation);
        var failure = CommandFailures.Of("sysctl -w vm.compact_memory=1", outcome);
        var said = Reading.Combine(before, after, (b, a) => string.Create(CultureInfo.InvariantCulture, $"free order-7 blocks {b.BlocksOrder7Plus} before, {a.BlocksOrder7Plus} after; order-4 {b.BlocksOrder4Plus} before, {a.BlocksOrder4Plus} after"));
        return new ActionRun(
            failure.Length == 0 ? 1 : 0,
            null,
            "defragmentation, not freeing: the bytes in free order-7 blocks of zone Normal, read from /proc/buddyinfo before and after",
            Order7Bytes(before),
            Order7Bytes(after),
            [],
            commands.Ran,
            failure)
        {
            Notes = [said.IsAvailable ? said.ValueOr(string.Empty) : $"the free blocks before / after were not read: {said.ReasonOrEmpty}"],
        };
    }

    private static long? Order7Bytes(Reading<Collectors.Procfs.Fragmentation> reading) =>
        reading is Reading<Collectors.Procfs.Fragmentation>.Available { Value: var f } ? f.BytesOrder7Plus : null;

    /// <summary>The EVENT, in words, or empty: no order-7 block, or an allocation failure since the last run.</summary>
    private static string Event(long order7Blocks, Reading<int> failures) => (order7Blocks, failures) switch
    {
        (0, _) => "event: no free order-7 block (512 KiB) left in zone Normal - the next VMBus allocation fails (plan 4.1)",
        (_, Reading<int>.Available { Value: > 0 } n) => string.Create(CultureInfo.InvariantCulture, $"event: {n.Value} page allocation failure(s) in the kernel log since the last run (plan 4.1)"),
        _ => string.Empty,
    };

    /// <summary>The kernel log's <c>page allocation failure</c> lines since the newest recorded run (or the timer's period).</summary>
    private static async Task<Reading<int>> AllocationFailuresAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        var history = RunHistory.Read(context.Paths, context.Files).Records;
        var since = history.Count > 0 ? history[^1].StartedAt : context.Clock.GetUtcNow() - Collect.CollectRun.DefaultWindow;
        var command = SystemdCommands.Search(since, new JournalScope.Kernel(), HealthCollector.AllocationFailure);
        var outcome = await commands.AsRunner().RunAsync(command.ToRequest(), cancellationToken).ConfigureAwait(false);
        return HealthParsers.SearchMatches(command, outcome).Map(lines => lines.Count);
    }
}
