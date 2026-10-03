using System.Globalization;

using WslCare.Core.Collectors;
using WslCare.Core.Config;
using WslCare.Core.Hosting;
using WslCare.Core.Processes;
using WslCare.Core.Processes.Policy;
using WslCare.Core.Thresholds;

namespace WslCare.Core.Actions.Memory;

/// <summary>
/// A1 (plan §5 as amended by §15c #3): <c>sync</c>, then <c>sysctl -w vm.drop_caches=1</c> — two argv invocations, no shell —
/// so the kernel gives the CLEAN page cache back, and WSL can return those pages to Windows. Never any other value (the
/// never-list refuses 2 and 3 whatever asks). Auto trigger (plan §4.1): <c>MemAvailable</c> below
/// <c>thresholds.memAvailableActPercent</c>, or the page cache above 12 GiB while less than 30 % is available. On the timer it
/// waits for an idle machine; a button runs it at once (plan §5 <i>Heavy actions wait for idle</i>).
/// </summary>
/// <remarks>
/// <para><b>Preview</b> (live): <c>/proc/meminfo</c> now — the page cache (<c>Cached</c> + <c>Buffers</c>) is the most it can
/// give back (dirty and mapped pages stay), and the available share the trigger reads.</para>
/// <para><b>Measured</b>: the page cache and <c>MemAvailable</c> read before and after. A1 frees MEMORY, not disk: the run's
/// <see cref="ActionRun.FreedBytes"/> stays unknown (as A11's), and the before / after figures are the page cache.</para>
/// <para><c>sync</c> is a bare name resolved on <c>PATH</c> like every tool (<c>/bin/sync</c> on Ubuntu is
/// <c>/usr/bin/sync</c> through the merged <c>/usr</c>); a <c>sync</c> that fails stops A1 before the drop — the drop is
/// only asked of a flushed cache.</para>
/// </remarks>
public sealed class CacheDrop : ICleanupAction
{
    /// <summary>The facts the trigger reads: the available share in tenths of a percent, and the page cache in bytes.</summary>
    public const string AvailablePermilleFact = "availablePermille";

    public const string PageCacheBytesFact = "pageCacheBytes";

    private const double Gib = 1024d * 1024 * 1024;

    public static readonly CommandTemplate Sync = new("sync", CommandScope.Machine, "sync", [], TimeSpan.FromMinutes(2), CommandRequest.DefaultOutputCapChars);

    /// <summary>The value is a LITERAL: no slot can ask for another (plan §15c #3; the property test plants the wrong one).</summary>
    public static readonly CommandTemplate DropCaches = new(
        "sysctl-drop-caches",
        CommandScope.Machine,
        "sysctl",
        [new ArgPart.Literal("-w"), new ArgPart.Literal("vm.drop_caches=1")],
        TimeSpan.FromSeconds(30),
        CommandRequest.DefaultOutputCapChars);

    public ActionId Id { get; } = ActionId.Find("A1")!;

    public string Summary => "sync, then sysctl -w vm.drop_caches=1: give the clean page cache back (never any other value)";

    public CommandScope Scope => CommandScope.Machine;

    public IdleRule Idle => IdleRule.TimerOnly;

    public IReadOnlyList<HostSide> Sides { get; } = [HostSide.Wsl];

    public IReadOnlyList<CommandTemplate> Commands { get; } = [Sync, DropCaches];

    public Task<ActionPreview> PreviewAsync(ActionContext context, ActionCommands commands, CancellationToken cancellationToken)
    {
        const string what = "sync, then sysctl -w vm.drop_caches=1: the clean page cache is given back (rebuilt on demand)";
        var figures = Reading.Combine(
            MemoryNow.Read(context).Bind(m => m.PageCache),
            MemoryNow.Read(context).Bind(m => m.AvailablePercent),
            (cache, available) => (cache, available));
        if (figures is not Reading<(long Cache, double Available)>.Available { Value: var f })
        {
            return Task.FromResult(ActionPreview.Unavailable(what, figures.ReasonOrEmpty));
        }

        var facts = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            [AvailablePermilleFact] = (long)Math.Round(f.Available * 10),
            [PageCacheBytesFact] = f.Cache,
        };
        IReadOnlyList<ActionItem> items = [new ActionItem("page cache", "Cached + Buffers", f.Cache, string.Create(CultureInfo.InvariantCulture, $"MemAvailable {f.Available:0.0} %"))];
        return Task.FromResult(ActionPreview.Of(what, 1, null, "/proc/meminfo now: the page cache (the item) is the most it can give back (dirty and mapped pages stay) - memory, not disk, so no bytes are counted", facts, string.Empty, items));
    }

    /// <summary>Plan §4.1: available below the act threshold, or a page cache above 12 GiB with less than 30 % available.</summary>
    public TriggerDecision Trigger(ActionPreview preview, EffectiveConfig config)
    {
        if (!preview.Facts.TryGetValue(AvailablePermilleFact, out var permille) || !preview.Facts.TryGetValue(PageCacheBytesFact, out var cache))
        {
            return new TriggerDecision(false, "the memory figures were not read");
        }

        var act = config.Int(ConfigKeys.Thresholds.MemAvailableActPercent);
        var available = permille / 10.0;
        var said = string.Create(CultureInfo.InvariantCulture, $"MemAvailable {available:0.0} %, page cache {cache / Gib:0.0} GiB; the trigger is available < {act} % (thresholds.memAvailableActPercent), or a page cache > {ThresholdRules.PageCacheActGib:0} GiB with available < {ThresholdRules.PageCacheActAvailablePercent:0} %");
        return new TriggerDecision(Fires(available, cache, act), said);
    }

    private static bool Fires(double availablePercent, long cacheBytes, int actPercent) =>
        availablePercent < actPercent || (cacheBytes > ThresholdRules.PageCacheActGib * Gib && availablePercent < ThresholdRules.PageCacheActAvailablePercent);

    public async Task<ActionRun> RunAsync(ActionContext context, ActionPreview preview, ActionCommands commands, CancellationToken cancellationToken)
    {
        var before = MemoryNow.Read(context);
        var sync = await commands.RunAsync(Sync, [], cancellationToken).ConfigureAwait(false);
        if (CommandFailures.Of("sync", sync) is { Length: > 0 } syncFailed)
        {
            return new ActionRun(0, null, "nothing dropped: sync failed, and the drop is only asked of a flushed cache", null, null, [], commands.Ran, syncFailed);
        }

        var drop = await commands.RunAsync(DropCaches, [], cancellationToken).ConfigureAwait(false);
        var after = MemoryNow.Read(context);
        var failure = CommandFailures.Of("sysctl -w vm.drop_caches=1", drop);
        var cacheBefore = before.Bind(m => m.PageCache);
        var cacheAfter = after.Bind(m => m.PageCache);
        return new ActionRun(
            failure.Length == 0 ? 1 : 0,
            null,
            "memory, not disk: the page cache (Cached + Buffers) read from /proc/meminfo before and after",
            Known(cacheBefore),
            Known(cacheAfter),
            [],
            commands.Ran,
            failure)
        {
            Notes = [Said("MemAvailable", before.Bind(m => m.AvailablePercent), after.Bind(m => m.AvailablePercent))],
        };
    }

    private static long? Known(Reading<long> bytes) => bytes.IsAvailable ? bytes.ValueOr(0) : null;

    private static string Said(string what, Reading<double> before, Reading<double> after) =>
        Reading.Combine(before, after, (b, a) => string.Create(CultureInfo.InvariantCulture, $"{what} {b:0.0} % before, {a:0.0} % after")) is Reading<string>.Available { Value: var text }
            ? text
            : $"{what} before / after not read: {before.ReasonOrEmpty}{after.ReasonOrEmpty}";
}
