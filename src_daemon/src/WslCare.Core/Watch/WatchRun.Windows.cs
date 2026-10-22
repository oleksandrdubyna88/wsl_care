using WslCare.Core.Actions;
using WslCare.Core.Actions.Engine;
using WslCare.Core.Collectors;
using WslCare.Core.Files;
using WslCare.Core.Hosting;
using WslCare.Core.Mcp;
using WslCare.Core.Records;

namespace WslCare.Core.Watch;

/// <summary>
/// The Windows binary's watch (WR-D4 of PLAN_windows_runner.md §3.2), what the per-user task <c>\wsl-care\watch</c> runs every
/// <c>mcpWatchdog.periodMinutes</c> through <c>tasks run watch</c>. An elevated process never gets here (<c>watch</c> refuses it, exit
/// 81). Under THE run lock, taken without waiting (busy = exit 75, a normal answer): the Windows MCP servers sampled into this user's
/// own CPU ledger (<see cref="McpCpuLedgerPlace.ForWindowsStatus"/>, unelevated — written under the ledger lock,
/// <see cref="McpCpuLedgerLock"/>), so the ledger advances every period; the lettered volumes read natively into <c>volumes.json</c> (E19.S2,
/// <see cref="VolumeSampleOutcome.Take"/>); then the run records' retention sweep
/// (<see cref="RunRetention.Sweep"/>, until now <c>collect</c>'s only). After the lock: with <c>--timer</c>, not observe-only, the A22
/// hook (<see cref="WindowsActing"/>, S7b.3 of PLAN_twenty_sessions_all_day.md) under the timer's dry-run decision on the Windows
/// paths. Until A22 is built nothing acts: the Windows watch samples only and writes NO history line (S2b's rule).
/// </summary>
public static partial class WatchRun
{
    /// <summary>THE A22 HOOK (S7b.3 of PLAN_twenty_sessions_all_day.md; WR-D4 of PLAN_windows_runner.md §3.11's boundary table): the
    /// Windows actions the watch's <c>--timer</c> branch lets act, in this order — EMPTY until S7b.3 builds A22. S7b.3 adds A22 here
    /// and its recorded act where <see cref="WindowsTimerBranch"/> answers an EMPTY reason — empty means every gate is open: act.
    /// Nothing else of the Windows watch changes.</summary>
    internal static IReadOnlyList<ActionId> WindowsActing { get; } = [];

    public const string NoTimerOnWindows = "without --timer the Windows watch records the evidence only; a Windows action would act from the \\wsl-care\\watch task (tasks run watch)";

    public const string NoWindowsAct = "no Windows action acts from the watch yet — A22 (S7b.3 of PLAN_twenty_sessions_all_day.md) is not built: the Windows watch samples only and writes no history line";

    /// <summary>The Windows watch's history reason: the agents' CPU history (A19's, A18's and A3's idle evidence) is the distro's.</summary>
    public const string NoAgentHistory = "the Windows watch keeps no agents' CPU history: that evidence is the distro's";

    private static async Task<WatchResult> RunOnWindowsAsync(WatchContext c, WindowsHostPaths windows, CancellationToken cancellationToken)
    {
        switch (await RunLock.TakeAsync(c.Paths, c.Files, TimeSpan.Zero, cancellationToken).ConfigureAwait(false))
        {
            case ExclusiveLock.Busy busy:
                var why = $"another run holds the run lock (another watch or an act): {busy.Reason}";
                return new WatchResult(WatchOutcome.Busy, why, McpCpuBaseline.NotRecorded(string.Empty, busy.Reason), NoAgentHistory);
            case ExclusiveLock.Held held:
                WatchResult sampled;
                using (held.Handle)
                {
                    sampled = await SampleOnWindowsAsync(c, windows, cancellationToken).ConfigureAwait(false);
                }

                return sampled with { Reason = WindowsTimerBranch(c) };
            default:
                throw new System.Diagnostics.UnreachableException("ExclusiveLock is a closed set");
        }
    }

    /// <summary>Under the run lock: the Windows MCP servers into this user's ledger, the lettered volumes into <c>volumes.json</c> (E19.S2), then the
    /// run records' retention sweep.</summary>
    private static async Task<WatchResult> SampleOnWindowsAsync(WatchContext c, WindowsHostPaths windows, CancellationToken cancellationToken)
    {
        var place = McpCpuLedgerPlace.ForWindowsStatus(windows, elevated: false);
        var collector = new WindowsMcpCollector(c.WindowsProcesses, c.Clock, c.Wait, new WindowsCpuLedger.Kept(c.Files, place, c.WindowsBoot));
        var mcp = await collector.SampleAsync(c.Loaded.Config, cancellationToken).ConfigureAwait(false);
        var ledger = mcp is Reading<WindowsMcpSample>.Available { Value: var sample } ? sample.Baseline : McpCpuBaseline.NotRecorded(place.FileOrEmpty, mcp.ReasonOrEmpty);
        // E19.S2: the lettered volumes, read natively (no process) after the MCP sample and stamped with the instant AFTER it — the CPU window
        // is behind us. A sample that cannot be written is in the answer and never ends the run.
        var volumes = VolumeSampleOutcome.Take(c.WindowsVolumes, new VolumeSampleStore(windows, c.Files), c.Clock.GetUtcNow());
        var swept = RunRetention.TrySweep(c.Paths, c.Files, c.Clock.GetUtcNow());
        return new WatchResult(WatchOutcome.Sampled, string.Empty, ledger, NoAgentHistory) { Swept = [swept], Volumes = volumes };
    }

    /// <summary>The <c>--timer</c> branch's gates, in order: the first that is closed says why; EMPTY when every gate is open (the A22
    /// act's place, unreachable while <see cref="WindowsActing"/> is empty). A watch with nothing to act on stops before
    /// the dry-run decision, so it never starts the dry-run week (<see cref="DryRunWindow.Decide"/> writes its stamp): the week stays a
    /// week of a Windows action's own decisions, as the distro's watch keeps it.</summary>
    private static string WindowsTimerBranch(WatchContext c) => c switch
    {
        { Timer: false } => NoTimerOnWindows,
        { Loaded.IsObserveOnly: true } => ActionEngine.ObserveOnlyReason,
        _ when WindowsActing.Count == 0 => NoWindowsAct,
        _ => WindowsDryReason(c),
    };

    /// <summary>The timer's dry-run decision on the Windows paths (its own first-week stamp under <c>%LOCALAPPDATA%\wsl-care</c>);
    /// empty when it says real.</summary>
    private static string WindowsDryReason(WatchContext c) =>
        DryRunWindow.Decide(RunTrigger.Timer, c.Loaded.Config, c.Paths, c.Files, c.Clock.GetUtcNow()) is { DryRun: true } dry
            ? $"a dry run ({dry.Reason}): the Windows watch only samples"
            : string.Empty;
}
